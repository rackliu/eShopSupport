﻿using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.SemanticKernel.Memory;

namespace eShopSupport.Backend.Data;

public class ProductManualSemanticSearch(ITextEmbeddingGenerationService embedder, HttpClient httpClient)
{
    private const string ManualCollectionName = "manuals";

    public async Task<IReadOnlyList<MemoryQueryResult>> SearchAsync(int productId, string query)
    {
        var embedding = await embedder.GenerateEmbeddingAsync(query);
        var response = await httpClient.PostAsync($"http://vector-db/collections/{ManualCollectionName}/points/search",
            JsonContent.Create(new {
                vector = embedding,
                with_payload = new[] { "id", "text" },
                limit = 3,
                filter = new
                {
                    must = new[]
                    {
                        new { key = "additional_metadata", match = new { value = $"productid:{productId}" } }
                    }
                }
            }));
        var responseParsed = await response.Content.ReadFromJsonAsync<QdrantResult>();

        return responseParsed!.Result.Select(r => new MemoryQueryResult(
            new MemoryRecordMetadata(true, r.Payload.Id, r.Payload.Text, "", "", ""),
            r.Score,
            null)).ToList();
    }

    public static async Task EnsureSeedDataImportedAsync(IServiceProvider services)
    {
        var importDataFromDir = Environment.GetEnvironmentVariable("ImportInitialDataDir");
        if (!string.IsNullOrEmpty(importDataFromDir))
        {
            using var scope = services.CreateScope();

            var semanticMemory = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
            var collections = semanticMemory.GetCollectionsAsync();

            if (!(await HasAnyAsync(collections)))
            {
                await semanticMemory.CreateCollectionAsync(ManualCollectionName);

                using var fileStream = File.OpenRead(Path.Combine(importDataFromDir, "manual-chunks.json"));
                var manualChunks = JsonSerializer.DeserializeAsyncEnumerable<ManualChunk>(fileStream);
                await foreach (var chunkChunk in ReadChunkedAsync(manualChunks, 1000))
                {
                    var mappedRecords = chunkChunk.Select(chunk =>
                    {
                        var id = chunk!.ParagraphId.ToString();
                        var metadata = new MemoryRecordMetadata(false, id, chunk.Text, "", "", $"productid:{chunk.ProductId}");
                        var embedding = MemoryMarshal.Cast<byte, float>(new ReadOnlySpan<byte>(chunk.Embedding)).ToArray();
                        return new MemoryRecord(metadata, embedding, null);
                    });

                    await foreach (var _ in semanticMemory.UpsertBatchAsync(ManualCollectionName, mappedRecords)) { }
                }
            }
        }
    }

    private static async Task<bool> HasAnyAsync<T>(IAsyncEnumerable<T> asyncEnumerable)
    {
        await foreach (var item in asyncEnumerable)
        {
            return true;
        }

        return false;
    }

    private static async IAsyncEnumerable<IEnumerable<T>> ReadChunkedAsync<T>(IAsyncEnumerable<T> source, int chunkLength)
    {
        var buffer = new T[chunkLength];
        var index = 0;
        await foreach (var item in source)
        {
            buffer[index++] = item;
            if (index == chunkLength)
            {
                yield return new ArraySegment<T>(buffer, 0, index);
                index = 0;
            }
        }

        if (index > 0)
        {
            yield return new ArraySegment<T>(buffer, 0, index);
        }
    }

    class QdrantResult
    {
        public required QdrantResultEntry[] Result { get; set; }
    }

    class QdrantResultEntry
    {
        public float Score { get; set; }
        public required QdrantResultEntryPayload Payload { get; set; }
    }

    class QdrantResultEntryPayload
    {
        public required string Id { get; set; }
        public required string Text { get; set; }
    }
}
