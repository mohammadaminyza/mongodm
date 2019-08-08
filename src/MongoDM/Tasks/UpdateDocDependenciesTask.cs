﻿using Digicando.MongoDM.Models;
using Digicando.MongoDM.Repositories;
using Digicando.MongoDM.Serialization.Modifiers;
using Hangfire.Server;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace Digicando.MongoDM.Tasks
{
    class UpdateDocDependenciesTask : IUpdateDocDependenciesTask
    {
        // Fields.
        private readonly IDBContextBase dbContext;
        private readonly ISerializerModifierAccessor serializerModifierAccessor;

        // Constructors.
        public UpdateDocDependenciesTask(
            IDBContextBase dbContext,
            ISerializerModifierAccessor serializerModifierAccessor)
        {
            this.dbContext = dbContext;
            this.serializerModifierAccessor = serializerModifierAccessor;
        }

        // Methods.
        public Task Run_NOTGENERICPROXY_Async(
            PerformContext performContext,
            Type modelType,
            Type keyType,
            IEnumerable<string> idPaths,
            object modelId)
        {
            return typeof(UpdateDocDependenciesTask).GetMethod(nameof(RunAsync), BindingFlags.NonPublic | BindingFlags.Instance)
                .MakeGenericMethod(modelType, keyType)
                .Invoke(this, new object[] { idPaths, modelId }) as Task;
        }

        // Helpers.
        private async Task RunAsync<TModel, TKey>(
            IEnumerable<string> idPaths,
            TKey modelId)
            where TModel : class, IEntityModel<TKey>
        {
            // Get repository.
            if (!dbContext.ModelCollectionRepositoryMap.ContainsKey(typeof(TModel)))
                return;
            var repository = dbContext.ModelCollectionRepositoryMap[typeof(TModel)] as ICollectionRepository<TModel, TKey>;

            HashSet<TKey> upgradedDocumentsId = new HashSet<TKey>();
            using (serializerModifierAccessor.EnableReferenceSerializerModifier(true))
            using (serializerModifierAccessor.EnableCacheSerializerModifier(true))
            {
                foreach (var idPath in idPaths)
                {
                    using (var cursor = await repository.FindAsync(
                        Builders<TModel>.Filter.Eq(idPath, modelId),
                        new FindOptions<TModel, TModel> { NoCursorTimeout = true }))
                    {
                        // Load and replace.
                        while (await cursor.MoveNextAsync())
                            foreach (var model in cursor.Current)
                            {
                                if (!upgradedDocumentsId.Contains(model.Id))
                                {
                                    try
                                    {
                                        // Replace on db.
                                        await repository.ReplaceAsync(model, false);
                                    }
                                    catch { }

                                    // Add id to upgraded list.
                                    upgradedDocumentsId.Add(model.Id);
                                }
                            }
                    }
                }
            }
        }
    }
}
