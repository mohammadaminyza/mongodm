﻿using Etherna.MongODM.Models;
using Etherna.MongODM.ProxyModels;
using Etherna.MongODM.Serialization.Modifiers;
using Etherna.MongODM.Utility;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Etherna.MongODM.Serialization.Serializers
{
    public class ReferenceSerializer<TModelBase, TKey> :
        SerializerBase<TModelBase>, IBsonSerializer<TModelBase>, IBsonDocumentSerializer, IBsonIdProvider, IReferenceContainerSerializer
        where TModelBase : class, IEntityModel<TKey>
    {
        // Fields.
        private IDiscriminatorConvention _discriminatorConvention = default!;

        private readonly ReaderWriterLockSlim configLockAdapters = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly ReaderWriterLockSlim configLockClassMaps = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly ReaderWriterLockSlim configLockSerializers = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly IDbCache dbCache;
        private readonly IDbContext dbContext;
        private readonly IProxyGenerator proxyGenerator;
        private readonly ISerializerModifierAccessor serializerModifierAccessor;

        private readonly IDictionary<Type, IBsonSerializer> registeredAdapters = new Dictionary<Type, IBsonSerializer>();
        private readonly IDictionary<Type, BsonClassMap> registeredClassMaps = new Dictionary<Type, BsonClassMap>();
        private readonly IDictionary<Type, IBsonSerializer> registeredSerializers = new Dictionary<Type, IBsonSerializer>();

        // Constructors.
        public ReferenceSerializer(
            IDbContext dbContext,
            bool useCascadeDelete)
        {
            this.dbCache = dbContext.DBCache;
            this.proxyGenerator = dbContext.ProxyGenerator;
            this.serializerModifierAccessor = dbContext.SerializerModifierAccessor;
            this.dbContext = dbContext;
            UseCascadeDelete = useCascadeDelete;
        }

        // Properties.
        public IEnumerable<BsonClassMap> ContainedClassMaps => registeredClassMaps.Values;
        public IDiscriminatorConvention DiscriminatorConvention
        {
            get
            {
                if (_discriminatorConvention == null)
                    _discriminatorConvention = BsonSerializer.LookupDiscriminatorConvention(typeof(TModelBase));
                return _discriminatorConvention;
            }
        }
        public bool? UseCascadeDelete { get; }

        // Methods.
        public override TModelBase Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        {
            // Check bson type.
            var bsonReader = context.Reader;
            var bsonType = bsonReader.GetCurrentBsonType();
            switch (bsonType)
            {
                case BsonType.Document:
                    break;
                case BsonType.Null:
                    bsonReader.ReadNull();
                    return null!;
                default:
                    var message = $"Expected a nested document representing the serialized form of a {nameof(TModelBase)} value, but found a value of type {bsonType} instead.";
                    throw new InvalidOperationException(message);
            }

            // Get actual type.
            var actualType = DiscriminatorConvention.GetActualType(bsonReader, args.NominalType);

            // Deserialize object.
            var serializer = GetSerializer(actualType);
            var model = serializer.Deserialize(context, args) as TModelBase;

            // Process model.
            if (model != null)
            {
                var id = model.Id;
                if (id == null) //ignore refered instances without id
                    return null!;

                // Check if model as been loaded in cache.
                if (dbCache.LoadedModels.ContainsKey(id) &&
                    !serializerModifierAccessor.IsNoCacheEnabled)
                {
                    var cachedModel = (TModelBase)dbCache.LoadedModels[id];

                    if (((IReferenceable)cachedModel).IsSummary)
                    {
                        // Execute merging between summary models.
                        var sourceMembers = ((IReferenceable)model).SettedMemberNames
                            .Except(((IReferenceable)cachedModel).SettedMemberNames)
                            .Select(memberName => cachedModel.GetType().GetMember(memberName).Single())
                            .ToArray();

                        //temporary disable auditing
                        ((IAuditable)cachedModel).DisableAuditing();

                        foreach (var member in sourceMembers)
                        {
                            var value = ReflectionHelper.GetValue(model, member);
                            ReflectionHelper.SetValue(cachedModel, member, value);
                        }

                        //reenable auditing
                        ((IAuditable)cachedModel).EnableAuditing();

                        ((IReferenceable)cachedModel).SetAsSummary(sourceMembers.Select(m => m.Name));
                    }

                    // Return the cached model.
                    model = cachedModel;
                }
                else
                {
                    // Set model as summarizable.
                    if (serializerModifierAccessor.IsReadOnlyReferencedIdEnabled)
                    {
                        ((IReferenceable)model).ClearSettedMembers();
                        ((IReferenceable)model).SetAsSummary(new[] { nameof(model.Id) });
                    }
                    else
                    {
                        ((IReferenceable)model).SetAsSummary(((IReferenceable)model).SettedMemberNames);
                    }

                    // Enable auditing.
                    ((IAuditable)model).EnableAuditing();

                    // Add in cache.
                    if (!serializerModifierAccessor.IsNoCacheEnabled)
                        dbCache.AddModel(model.Id!, model);
                }
            }

            return model!;
        }

        public IBsonSerializer<TModel> GetAdapter<TModel>()
            where TModel : class, TModelBase
        {
            configLockAdapters.EnterReadLock();
            try
            {
                if (registeredAdapters.ContainsKey(typeof(TModel)))
                {
                    return (IBsonSerializer<TModel>)registeredAdapters[typeof(TModel)];
                }
            }
            finally
            {
                configLockAdapters.ExitReadLock();
            }

            configLockAdapters.EnterWriteLock();
            try
            {
                if (!registeredAdapters.ContainsKey(typeof(TModel)))
                {
                    registeredAdapters.Add(typeof(TModel), new ReferenceSerializerAdapter<TModelBase, TModel, TKey>(this));
                }
                return (IBsonSerializer<TModel>)registeredAdapters[typeof(TModel)];
            }
            finally
            {
                configLockAdapters.ExitWriteLock();
            }
        }

        public bool GetDocumentId(object document, out object id, out Type idNominalType, out IIdGenerator idGenerator)
        {
            IsProxyClassType(document, out Type documentType);
            var serializer = (IBsonIdProvider)GetSerializer(documentType);
            return serializer.GetDocumentId(document, out id, out idNominalType, out idGenerator);
        }
        
        public ReferenceSerializer<TModelBase, TKey> RegisterType<TModel>(Action<BsonClassMap<TModel>>? classMapInitializer = null)
            where TModel : class
        {
            // Initialize class map.
            var classMap = CreateBsonClassMap(classMapInitializer ?? (cm => cm.AutoMap()));

            // Set creator.
            if (!typeof(TModel).IsAbstract)
                classMap.SetCreator(() => proxyGenerator.CreateInstance<TModel>(dbContext));

            // Add info to dictionary of registered types.
            configLockClassMaps.EnterWriteLock();
            try
            {
                registeredClassMaps.Add(typeof(TModel), classMap);
            }
            finally
            {
                configLockClassMaps.ExitWriteLock();
            }

            // Return this for cascade use.
            return this;
        }

        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TModelBase value)
        {
            // Check value type.
            if (value == null)
            {
                context.Writer.WriteNull();
                return;
            }

            // Identify class map.
            bool useProxyClass = IsProxyClassType(value, out Type valueType);

            BsonClassMap classMap;
            configLockClassMaps.EnterReadLock();
            try
            {
                if (!registeredClassMaps.ContainsKey(valueType))
                {
                    throw new InvalidOperationException("Can't identify right class map");
                }
                classMap = registeredClassMaps[valueType];
            }
            finally
            {
                configLockClassMaps.ExitReadLock();
            }

            // Remove proxy class.
            if (useProxyClass)
            {
                var constructor = valueType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, new Type[0], null) ??
                    valueType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[0], null);
                var newModel = (TModelBase)constructor.Invoke(new object[0]);
                ReflectionHelper.CloneModel(value, newModel, from mMap in classMap.AllMemberMaps
                                                             where mMap != classMap.ExtraElementsMemberMap
                                                             select mMap.MemberInfo as PropertyInfo);
                value = newModel;
            }

            // Clear extra elements.
            (value as IModel)?.ExtraElements?.Clear();

            // Serialize object.
            var serializer = GetSerializer(valueType);
            serializer.Serialize(context, args, value);
        }

        public void SetDocumentId(object document, object id)
        {
            IsProxyClassType(document, out Type documentType);
            var serializer = (IBsonIdProvider)GetSerializer(documentType);
            serializer.SetDocumentId(document, id);
        }

        public bool TryGetMemberSerializationInfo(string memberName, out BsonSerializationInfo serializationInfo)
        {
            // Identify class map and get information
            configLockClassMaps.EnterReadLock();
            try
            {
                var modelType = (from pair in registeredClassMaps
                                 where pair.Value.GetMemberMap(memberName) != null
                                 select pair.Key).FirstOrDefault();
                var serializer = (IBsonDocumentSerializer)GetSerializer(modelType);
                return serializer.TryGetMemberSerializationInfo(memberName, out serializationInfo);
            }
            finally
            {
                configLockClassMaps.ExitReadLock();
            }
        }

        // Helpers.
        private BsonClassMap<TModel> CreateBsonClassMap<TModel>(Action<BsonClassMap<TModel>> classMapInitializer)
        {
            BsonClassMap<TModel> classMap = new BsonClassMap<TModel>(classMapInitializer);
            var baseType = typeof(TModel).BaseType;
            configLockClassMaps.EnterReadLock();
            try
            {
                if (registeredClassMaps.ContainsKey(baseType))
                {
                    // Inject base class map.
                    typeof(BsonClassMap).GetField("_baseClassMap", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(classMap, registeredClassMaps[baseType]);
                }
            }
            finally
            {
                configLockClassMaps.ExitReadLock();
            }

            classMap.Freeze();
            return classMap;
        }

        private IBsonSerializer GetSerializer(Type actualType)
        {
            configLockSerializers.EnterReadLock();
            try
            {
                if (registeredSerializers.ContainsKey(actualType))
                {
                    return registeredSerializers[actualType];
                }
            }
            finally
            {
                configLockSerializers.ExitReadLock();
            }

            configLockSerializers.EnterWriteLock();
            try
            {
                if (!registeredSerializers.ContainsKey(actualType))
                {
                    BsonClassMap classMap;
                    configLockClassMaps.EnterReadLock();
                    try
                    {
                        if (!registeredClassMaps.ContainsKey(actualType))
                        {
                            throw new InvalidOperationException("Can't identify right class map");
                        }
                        classMap = registeredClassMaps[actualType];
                    }
                    finally
                    {
                        configLockClassMaps.ExitReadLock();
                    }
                    var classMapSerializerDefinition = typeof(BsonClassMapSerializer<>);
                    var classMapSerializerType = classMapSerializerDefinition.MakeGenericType(actualType);
                    var serializer = (IBsonSerializer)Activator.CreateInstance(classMapSerializerType, classMap);
                    registeredSerializers.Add(actualType, serializer);
                }
                return registeredSerializers[actualType];
            }
            finally
            {
                configLockSerializers.ExitWriteLock();
            }
        }

        private bool IsProxyClassType<TModel>(TModel value, out Type modelType)
        {
            modelType = value!.GetType();
            if (proxyGenerator.IsProxyType(modelType))
            {
                modelType = modelType.BaseType;
                return true;
            }
            return false;
        }
    }
}
