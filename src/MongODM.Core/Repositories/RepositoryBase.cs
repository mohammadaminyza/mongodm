﻿using Digicando.DomainHelper;
using Digicando.MongODM.Exceptions;
using Digicando.MongODM.Models;
using Digicando.MongODM.ProxyModels;
using Digicando.MongODM.Serialization;
using MoreLinq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#nullable enable
namespace Digicando.MongODM.Repositories
{
    public abstract class RepositoryBase<TModel, TKey> :
        IRepository<TModel, TKey>
        where TModel : class, IEntityModel<TKey>
    {
        // Initializer.
        public virtual void Initialize(IDbContext dbContext)
        {
            if (IsInitialized)
                throw new InvalidOperationException("Instance already initialized");
            DbContext = dbContext;

            IsInitialized = true;
        }

        // Properties.
        public IDbContext DbContext { get; private set; } = default!;
        public Type GetKeyType => typeof(TKey);
        public Type GetModelType => typeof(TModel);
        public bool IsInitialized { get; private set; }

        // Methods.
        public abstract Task BuildIndexesAsync(IDocumentSchemaRegister schemaRegister, CancellationToken cancellationToken = default);

        public virtual async Task CreateAsync(IEnumerable<TModel> models, CancellationToken cancellationToken = default)
        {
            await CreateOnDBAsync(models, cancellationToken);
            await DbContext.SaveChangesAsync();
        }

        public virtual async Task CreateAsync(TModel model, CancellationToken cancellationToken = default)
        {
            await CreateOnDBAsync(model, cancellationToken);
            await DbContext.SaveChangesAsync();
        }

        public async Task DeleteAsync(TKey id, CancellationToken cancellationToken = default)
        {
            var model = await FindOneAsync(id, cancellationToken: cancellationToken);
            await DeleteAsync(model, cancellationToken);
        }

        public virtual async Task DeleteAsync(TModel model, CancellationToken cancellationToken = default)
        {
            // Process cascade delete.
            var referencesIdsPaths = DbContext.DocumentSchemaRegister.GetModelEntityReferencesIds(typeof(TModel))
                .Where(d => d.UseCascadeDelete == true)
                .Where(d => d.EntityClassMapPath.Count() == 2) //ignore references of references
                .DistinctBy(d => d.FullPathToString())
                .Select(d => d.MemberPath);

            foreach (var idPath in referencesIdsPaths)
                await CascadeDeleteMembersAsync(model, idPath);

            // Unlink dependent models.
            model.DisposeForDelete();
            await DbContext.SaveChangesAsync();

            // Delete model.
            await DeleteOnDBAsync(model, cancellationToken);

            // Remove from cache.
            if (DbContext.DBCache.LoadedModels.ContainsKey(model.Id!))
                DbContext.DBCache.RemoveModel(model.Id!);
        }

        public async Task DeleteAsync(IEntityModel model, CancellationToken cancellationToken = default)
        {
            if (!(model is TModel castedModel))
                throw new InvalidEntityTypeException("Invalid model type");
            await DeleteAsync(castedModel, cancellationToken);
        }

        public virtual async Task<TModel> FindOneAsync(
            TKey id,
            CancellationToken cancellationToken = default)
        {
            if (DbContext.DBCache.LoadedModels.ContainsKey(id!))
            {
                var cachedModel = DbContext.DBCache.LoadedModels[id!] as TModel;
                if ((cachedModel as IReferenceable)?.IsSummary == false)
                    return cachedModel!;
            }

            return await FindOneOnDBAsync(id, cancellationToken);
        }

        public async Task<TModel?> TryFindOneAsync(
            TKey id,
            CancellationToken cancellationToken = default)
        {
            if (id == null)
            {
                return null;
            }

            try
            {
                return await FindOneAsync(id, cancellationToken);
            }
            catch (EntityNotFoundException)
            {
                return null;
            }
        }

        // Protected abstract methods.
        protected abstract Task CreateOnDBAsync(IEnumerable<TModel> models, CancellationToken cancellationToken);

        protected abstract Task CreateOnDBAsync(TModel model, CancellationToken cancellationToken);

        protected abstract Task DeleteOnDBAsync(TModel model, CancellationToken cancellationToken);

        protected abstract Task<TModel> FindOneOnDBAsync(TKey id, CancellationToken cancellationToken = default);

        // Helpers.
        private async Task CascadeDeleteMembersAsync(object currentModel, IEnumerable<EntityMember> idPath)
        {
            if (!idPath.Any())
                throw new ArgumentException("Member path can't be empty", nameof(idPath));

            var currentMember = idPath.First();
            var memberTail = idPath.Skip(1);

            if (currentMember.IsId)
            {
                //cascade delete model
                var repository = DbContext.RepositoryRegister.ModelRepositoryMap[currentModel.GetType().BaseType];
                try { await repository.DeleteAsync((IEntityModel)currentModel); }
                catch { }
            }
            else
            {
                //recursion on value
                var memberInfo = currentMember.MemberMap.MemberInfo;
                var memberValue = ReflectionHelper.GetValue(currentModel, memberInfo);
                if (memberValue == null)
                    return;

                if (memberValue is IEnumerable enumerableMemberValue) //if enumerable
                {
                    if (enumerableMemberValue is IDictionary dictionaryMemberValue)
                        enumerableMemberValue = dictionaryMemberValue.Values;

                    foreach (var itemValue in enumerableMemberValue.Cast<object>().ToArray())
                        await CascadeDeleteMembersAsync(itemValue, memberTail);
                }
                else
                {
                    await CascadeDeleteMembersAsync(memberValue, memberTail);
                }
            }
        }
    }
}