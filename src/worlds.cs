﻿// ----------------------------------------------------------------------------
// The MIT License
// Lightweight ECS framework https://github.com/Leopotam/ecslite
// Copyright (c) 2021-2022 Leopotam <leopotam@gmail.com>
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif


namespace Leopotam.EcsLite {
#if ENABLE_IL2CPP
    [Il2CppSetOption (Option.NullChecks, false)]
    [Il2CppSetOption (Option.ArrayBoundsChecks, false)]
#endif
    public partial class EcsWorld {
#if ECS_INT_PACKED

        public const int MASK_ENTITY = 0b00000000001111111111111111111111;
        public const int MASK_GEN =    0b00000011110000000000000000000000;
        public const int MASK_WORLD =  0b01111100000000000000000000000000;
        public const int SHIFT_GEN = 22;
        public const int SHIFT_WORLD = 26;

        public const int MAX_WORLDS = (1 << 5) - 1;
        public const int MAX_GEN = (1 << 4) - 1;
        public const int MAX_ENTITIES = (1 << 22) - 1;

        public static EcsWorld[] worlds = new EcsWorld[MAX_WORLDS];

        /// <summary>
        /// Register this world with its specific idx
        /// </summary>
        /// <param name="idx"></param>
        /// <param name="world"></param>
        protected static void RegisterWorlds(int idx, EcsWorld world) {
            if (idx >= MAX_WORLDS) {
                throw new Exception("You ");
            }
            worlds[idx] = world;
        }

        public static void DestroyWorlds(bool alsoDestruct = false) {
            for (int i=0,count=worlds.Length;i<count;i++) {
                if (worlds[i] == null) {
                    continue;
                }
                worlds[i].Destroy();
            }
        }

        public int worldBitmask;
        public int worldIdx;


#endif
        public EntityData[] Entities;
        public int _entitiesCount;
        public int[] _recycledEntities;
        public int _recycledEntitiesCount;
        public IEcsPool[] _pools;
        public int _poolsCount;
        protected readonly int _poolDenseSize;
        protected readonly int _poolRecycledSize;
        protected readonly Dictionary<Type, IEcsPool> _poolHashes;
        protected readonly Dictionary<int, EcsFilter> _hashedFilters;
        protected readonly List<EcsFilter> _allFilters;
        protected List<EcsFilter>[] _filtersByIncludedComponents;
        protected List<EcsFilter>[] _filtersByExcludedComponents;
        protected Mask[] _masks;
        protected int _masksCount;

        protected bool _destroyed;
#if LEOECSLITE_WORLD_EVENTS
        protected List<IEcsWorldEventListener> _eventListeners;

        public void AddEventListener (IEcsWorldEventListener listener) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (listener == null) { throw new Exception ("Listener is null."); }
#endif
            _eventListeners.Add (listener);
        }

        public void RemoveEventListener (IEcsWorldEventListener listener) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (listener == null) { throw new Exception ("Listener is null."); }
#endif
            _eventListeners.Remove (listener);
        }

        public void RaiseEntityChangeEvent (int entity) {
            for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
                _eventListeners[ii].OnEntityChanged (entity);
            }
        }
#endif
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
        readonly List<int> _leakedEntities = new List<int> (512);

        internal bool CheckForLeakedEntities () {
            if (_leakedEntities.Count > 0) {
                for (int i = 0, iMax = _leakedEntities.Count; i < iMax; i++) {
                    ref var entityData = ref Entities[_leakedEntities[i]];
                    if (entityData.Gen > 0 && entityData.ComponentsCount == 0) {
                        return true;
                    }
                }
                _leakedEntities.Clear ();
            }
            return false;
        }
#endif

#if ECS_INT_PACKED
        public EcsWorld (int _worldIdx,in Config cfg = default) {
            RegisterWorlds(_worldIdx, this);
            worldIdx = _worldIdx;
            worldBitmask = _worldIdx << SHIFT_WORLD;
#else
        public EcsWorld (in Config cfg = default) {
#endif
            // entities.
            var capacity = cfg.Entities > 0 ? cfg.Entities : Config.EntitiesDefault;
            Entities = new EntityData[capacity];
            capacity = cfg.RecycledEntities > 0 ? cfg.RecycledEntities : Config.RecycledEntitiesDefault;
            _recycledEntities = new int[capacity];
            _entitiesCount = 0;
            _recycledEntitiesCount = 0;
            // pools.
            capacity = cfg.Pools > 0 ? cfg.Pools : Config.PoolsDefault;
            _pools = new IEcsPool[capacity];
            _poolHashes = new Dictionary<Type, IEcsPool> (capacity);
            _filtersByIncludedComponents = new List<EcsFilter>[capacity];
            _filtersByExcludedComponents = new List<EcsFilter>[capacity];
            _poolDenseSize = cfg.PoolDenseSize > 0 ? cfg.PoolDenseSize : Config.PoolDenseSizeDefault;
            _poolRecycledSize = cfg.PoolRecycledSize > 0 ? cfg.PoolRecycledSize : Config.PoolRecycledSizeDefault;
            _poolsCount = 0;
            // filters.
            capacity = cfg.Filters > 0 ? cfg.Filters : Config.FiltersDefault;
            _hashedFilters = new Dictionary<int, EcsFilter> (capacity);
            _allFilters = new List<EcsFilter> (capacity);
            // masks.
            _masks = new Mask[64];
            _masksCount = 0;
#if LEOECSLITE_WORLD_EVENTS
            _eventListeners = new List<IEcsWorldEventListener> (4);
#endif
            _destroyed = false;
        }

        public void Destroy () {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (CheckForLeakedEntities ()) { throw new Exception ($"Empty entity detected before EcsWorld.Destroy()."); }
#endif
            _destroyed = true;
            for (var i = _entitiesCount - 1; i >= 0; i--) {
                ref var entityData = ref Entities[i];
                if (entityData.ComponentsCount > 0) {
                    DelEntity (i);
                }
            }
            _pools = Array.Empty<IEcsPool> ();
            _poolHashes.Clear ();
            _hashedFilters.Clear ();
            _allFilters.Clear ();
            _filtersByIncludedComponents = Array.Empty<List<EcsFilter>> ();
            _filtersByExcludedComponents = Array.Empty<List<EcsFilter>> ();
#if LEOECSLITE_WORLD_EVENTS
            for (var ii = _eventListeners.Count - 1; ii >= 0; ii--) {
                _eventListeners[ii].OnWorldDestroyed (this);
            }
#endif
            worlds[worldIdx] = null;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public bool IsAlive () {
            return !_destroyed;
        }


        public int NewEntity () {
            int entity;
            int gen = 0;
            if (_recycledEntitiesCount > 0) {
                entity = _recycledEntities[--_recycledEntitiesCount];
                ref var entityData = ref Entities[entity];
                gen = entityData.Gen = (short) -entityData.Gen;
            } else {
                // new entity.
                if (_entitiesCount == Entities.Length) {
                    // resize entities and component pools.
                    var newSize = _entitiesCount << 1;
                    Array.Resize (ref Entities, newSize);
                    for (int i = 0, iMax = _poolsCount; i < iMax; i++) {
                        _pools[i].Resize (newSize);
                    }
                    for (int i = 0, iMax = _allFilters.Count; i < iMax; i++) {
                        _allFilters[i].ResizeSparseIndex (newSize);
                    }
#if LEOECSLITE_WORLD_EVENTS
                    for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
                        _eventListeners[ii].OnWorldResized (newSize);
                    }
#endif
                }
                entity = _entitiesCount++;
                gen = Entities[entity].Gen = 1;
            }
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            _leakedEntities.Add (entity);
#endif
#if DEBUG && LEOECSLITE_WORLD_EVENTS
            for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
                _eventListeners[ii].OnEntityCreated (entity);
            }
#endif
#if ECS_INT_PACKED
            entity = PackEntity(entity, gen);
#endif
            return entity;
        }

#if ECS_INT_PACKED
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int PackEntity(int plainEntity) {
            int gen = GetEntityGen(plainEntity) << SHIFT_GEN;
            int packedEntity = worldBitmask | gen | plainEntity;
            return packedEntity;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int PackEntity(int plainEntity,int gen) {
            gen = gen << SHIFT_GEN;
            int packedEntity = worldBitmask | gen | plainEntity;
            return packedEntity;
        }

        /// <summary>
        /// Unpack and return values as ValueTuple
        /// </summary>
        /// <param name="packedEntity"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (int, int, int) UnpackEntity(int packedEntity) {
            // due to better inlining, dont using the specialized methods here. 
            int rawEntity = packedEntity & MASK_ENTITY;
            int worldID = (packedEntity & MASK_WORLD) >> SHIFT_WORLD;
            int gen = (packedEntity & MASK_GEN) >> SHIFT_GEN;
            return (rawEntity, worldID, gen);
        }

        /// <summary>
        /// Unpack and return values as ValueTuple
        /// </summary>
        /// <param name="packedEntity"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (int, T, int) UnpackEntityWithWorld<T>(int packedEntity) where T:EcsWorld {
            // due to better inlining, dont using the specialized methods here. 
            int rawEntity = packedEntity & MASK_ENTITY;
            int worldID = (packedEntity & MASK_WORLD) >> SHIFT_WORLD;
            EcsWorld world = worlds[worldID];
            int gen = (packedEntity & MASK_GEN) >> SHIFT_GEN;
            return (rawEntity, Unsafe.As<T>(world), gen);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetPackedGen(int packedEntity) {
            int gen = (packedEntity & MASK_GEN) >> SHIFT_GEN;
            return gen;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetPackedWorldID(int packedEntity) {
            int worldId = (packedEntity & MASK_WORLD) >> SHIFT_WORLD;
            return worldId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T GetPackedWorld<T>(int packedEntity) where T:EcsWorld {
            int worldId = (packedEntity & MASK_WORLD) >> SHIFT_WORLD;
            T world = Unsafe.As<T>(worlds[worldId]);
            return world;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetPackedRawEntityId(int packedEntity) {
            var rawEntity = packedEntity & MASK_ENTITY;
            return rawEntity;
        }
#endif

        public void DelEntity (int entity) {
#if ECS_INT_PACKED
            entity = EcsWorld.GetPackedRawEntityId(entity);
#endif
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (entity < 0 || entity >= _entitiesCount) { throw new Exception ("Cant touch destroyed entity."); }
#endif
            ref var entityData = ref Entities[entity];
            if (entityData.Gen < 0) {
                return;
            }
            // kill components.
            if (entityData.ComponentsCount > 0) {
                var idx = 0;
                while (entityData.ComponentsCount > 0 && idx < _poolsCount) {
                    for (; idx < _poolsCount; idx++) {
                        if (_pools[idx].Has (entity)) {
                            _pools[idx++].Del (entity);
                            break;
                        }
                    }
                }
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
                if (entityData.ComponentsCount != 0) { 
                    throw new Exception ($"Invalid components count on entity {entity} => {entityData.ComponentsCount}."); 
                }
#endif
                return;
            }
            entityData.Gen = (short) (entityData.Gen == short.MaxValue ? -1 : -(entityData.Gen + 1));
            if (_recycledEntitiesCount == _recycledEntities.Length) {
                Array.Resize (ref _recycledEntities, _recycledEntitiesCount << 1);
            }
            _recycledEntities[_recycledEntitiesCount++] = entity;
#if DEBUG && LEOECSLITE_WORLD_EVENTS
            for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
                _eventListeners[ii].OnEntityDestroyed (entity);
            }
#endif
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public int GetComponentsCount (int entity) {
#if ECS_INT_PACKED
            entity = EcsWorld.GetPackedRawEntityId(entity);
#endif
            return Entities[entity].ComponentsCount;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public short GetEntityGen (int entity) {
#if ECS_INT_PACKED
            entity = EcsWorld.GetPackedRawEntityId(entity);
#endif
            return Entities[entity].Gen;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public int GetAllocatedEntitiesCount () {
            return _entitiesCount;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public int GetWorldSize () {
            return Entities.Length;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public EntityData[] GetRawEntities () {
            return Entities;
        }

        public EcsPool<T> GetPool<T> () where T : struct {
            var poolType = typeof (T);
            if (_poolHashes.TryGetValue (poolType, out var rawPool)) {
                return (EcsPool<T>) rawPool;
            }
            var pool = new EcsPool<T> (this, _poolsCount, _poolDenseSize, Entities.Length, _poolRecycledSize);
            _poolHashes[poolType] = pool;
            if (_poolsCount == _pools.Length) {
                var newSize = _poolsCount << 1;
                Array.Resize (ref _pools, newSize);
                Array.Resize (ref _filtersByIncludedComponents, newSize);
                Array.Resize (ref _filtersByExcludedComponents, newSize);
            }
            _pools[_poolsCount++] = pool;
            return pool;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public IEcsPool GetPoolById (int typeId) {
            return typeId >= 0 && typeId < _poolsCount ? _pools[typeId] : null;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public IEcsPool GetPoolByType (Type type) {
            return _poolHashes.TryGetValue (type, out var pool) ? pool : null;
        }

        public int GetAllEntities (ref int[] entities) {
            var count = _entitiesCount - _recycledEntitiesCount;
            if (entities == null || entities.Length < count) {
                entities = new int[count];
            }
            var id = 0;
            for (int i = 0, iMax = _entitiesCount; i < iMax; i++) {
                ref var entityData = ref Entities[i];
                // should we skip empty entities here?
                if (entityData.Gen > 0 && entityData.ComponentsCount >= 0) {
                    entities[id++] = i;
                }
            }
            return count;
        }

        public int GetAllPools (ref IEcsPool[] pools) {
            var count = _poolsCount;
            if (pools == null || pools.Length < count) {
                pools = new IEcsPool[count];
            }
            Array.Copy (_pools, 0, pools, 0, _poolsCount);
            return _poolsCount;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public Mask Filter<T> () where T : struct {
            var mask = _masksCount > 0 ? _masks[--_masksCount] : new Mask (this);
            return mask.Inc<T> ();
        }

        public int GetComponents (int entity, ref object[] list) {
#if ECS_INT_PACKED
            entity = EcsWorld.GetPackedRawEntityId(entity);
#endif
            var itemsCount = Entities[entity].ComponentsCount;
            if (itemsCount == 0) { return 0; }
            if (list == null || list.Length < itemsCount) {
                list = new object[_pools.Length];
            }
            for (int i = 0, j = 0, iMax = _poolsCount; i < iMax; i++) {
                if (_pools[i].Has (entity)) {
                    list[j++] = _pools[i].GetRaw (entity);
                }
            }
            return itemsCount;
        }

        public object[] GetComponents(int entity) {
            object[] comps = null;
            GetComponents(entity, ref comps);
            return comps;
        }

        public int GetComponentTypes (int entity, ref Type[] list) {
#if ECS_INT_PACKED
            entity = EcsWorld.GetPackedRawEntityId(entity);
#endif
            var itemsCount = Entities[entity].ComponentsCount;
            if (itemsCount == 0) { return 0; }
            if (list == null || list.Length < itemsCount) {
                list = new Type[_pools.Length];
            }
            for (int i = 0, j = 0, iMax = _poolsCount; i < iMax; i++) {
                if (_pools[i].Has (entity)) {
                    list[j++] = _pools[i].GetComponentType ();
                }
            }
            return itemsCount;
        }


        /// <summary>
        /// Needs to be called with unpacked entity 
        /// </summary>
        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public bool IsEntityAliveInternal (int entity) {
            return entity >= 0 && entity < _entitiesCount && Entities[entity].Gen > 0;
        }

        (EcsFilter<T>, bool) GetFilterInternal<T> (Mask mask, int capacity = 16) where T:IFilterData {
            var hash = mask.Hash;
            var exists = _hashedFilters.TryGetValue (hash, out var filter);
            if (exists) { return ((EcsFilter<T>)filter, false); }
            filter = new EcsFilter<T> (this, mask, capacity, Entities.Length);
            _hashedFilters[hash] = filter;
            _allFilters.Add (filter);
            // add to component dictionaries for fast compatibility scan.
            for (int i = 0, iMax = mask.IncludeCount; i < iMax; i++) {
                var list = _filtersByIncludedComponents[mask.Include[i]];
                if (list == null) {
                    list = new List<EcsFilter> (8);
                    _filtersByIncludedComponents[mask.Include[i]] = list;
                }
                list.Add (filter);
            }
            for (int i = 0, iMax = mask.ExcludeCount; i < iMax; i++) {
                var list = _filtersByExcludedComponents[mask.Exclude[i]];
                if (list == null) {
                    list = new List<EcsFilter> (8);
                    _filtersByExcludedComponents[mask.Exclude[i]] = list;
                }
                list.Add (filter);
            }
            // scan exist entities for compatibility with new filter.
            for (int i = 0, iMax = _entitiesCount; i < iMax; i++) {
                ref var entityData = ref Entities[i];
                if (entityData.ComponentsCount > 0 && IsMaskCompatible (mask, i)) {
                    filter.AddEntity (i);
                }
            }
#if LEOECSLITE_WORLD_EVENTS
            for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
                _eventListeners[ii].OnFilterCreated (filter);
            }
#endif
            return ((EcsFilter<T>)filter, true);
        }

        /// <summary>
        /// Needs to be called with unpacked entity 
        /// </summary>
        public void OnEntityChangeInternal (int entity, int componentType, bool added) {
            var includeList = _filtersByIncludedComponents[componentType];
            var excludeList = _filtersByExcludedComponents[componentType];
            if (added) {
                // add component.
                if (includeList != null) {
                    foreach (var filter in includeList) {
                        if (IsMaskCompatible (filter.GetMask (), entity)) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
                            if (filter.SparseEntities[entity] > 0) { throw new Exception ("Entity already in filter."); }
#endif
                            filter.AddEntity (entity);
                        }
                    }
                }
                if (excludeList != null) {
                    foreach (var filter in excludeList) {
                        if (IsMaskCompatibleWithout (filter.GetMask (), entity, componentType)) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
                            if (filter.SparseEntities[entity] == 0) { throw new Exception ("Entity not in filter."); }
#endif
                            filter.RemoveEntity (entity);
                        }
                    }
                }
            } else {
                // remove component.
                if (includeList != null) {
                    foreach (var filter in includeList) {
                        if (IsMaskCompatible (filter.GetMask (), entity)) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
                            if (filter.SparseEntities[entity] == 0) { throw new Exception ("Entity not in filter."); }
#endif
                            filter.RemoveEntity (entity);
                        }
                    }
                }
                if (excludeList != null) {
                    foreach (var filter in excludeList) {
                        if (IsMaskCompatibleWithout (filter.GetMask (), entity, componentType)) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
                            if (filter.SparseEntities[entity] > 0) { throw new Exception ("Entity already in filter."); }
#endif
                            filter.AddEntity (entity);
                        }
                    }
                }
            }
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        bool IsMaskCompatible (Mask filterMask, int entity) {
            for (int i = 0, iMax = filterMask.IncludeCount; i < iMax; i++) {
                if (!_pools[filterMask.Include[i]].Has (entity)) {
                    return false;
                }
            }
            for (int i = 0, iMax = filterMask.ExcludeCount; i < iMax; i++) {
                if (_pools[filterMask.Exclude[i]].Has (entity)) {
                    return false;
                }
            }
            return true;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        bool IsMaskCompatibleWithout (Mask filterMask, int entity, int componentId) {
            for (int i = 0, iMax = filterMask.IncludeCount; i < iMax; i++) {
                var typeId = filterMask.Include[i];
                if (typeId == componentId || !_pools[typeId].Has (entity)) {
                    return false;
                }
            }
            for (int i = 0, iMax = filterMask.ExcludeCount; i < iMax; i++) {
                var typeId = filterMask.Exclude[i];
                if (typeId != componentId && _pools[typeId].Has (entity)) {
                    return false;
                }
            }
            return true;
        }

        public struct Config {
            public int Entities;
            public int RecycledEntities;
            public int Pools;
            public int Filters;
            public int PoolDenseSize;
            public int PoolRecycledSize;

            internal const int EntitiesDefault = 512;
            internal const int RecycledEntitiesDefault = 512;
            internal const int PoolsDefault = 512;
            internal const int FiltersDefault = 512;
            internal const int PoolDenseSizeDefault = 512;
            internal const int PoolRecycledSizeDefault = 512;
        }

#if ENABLE_IL2CPP
    [Il2CppSetOption (Option.NullChecks, false)]
    [Il2CppSetOption (Option.ArrayBoundsChecks, false)]
#endif
        public sealed class Mask {
            readonly EcsWorld _world;
            internal int[] Include;
            internal int[] Exclude;
            internal int IncludeCount;
            internal int ExcludeCount;
            internal int Hash;
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            bool _built;
#endif

            internal Mask (EcsWorld world) {
                _world = world;
                Include = new int[8];
                Exclude = new int[2];
                Reset ();
            }

            [MethodImpl (MethodImplOptions.AggressiveInlining)]
            void Reset () {
                IncludeCount = 0;
                ExcludeCount = 0;
                Hash = 0;
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
                _built = false;
#endif
            }

            [MethodImpl (MethodImplOptions.AggressiveInlining)]
            public Mask Inc<T> () where T : struct {
                var poolId = _world.GetPool<T> ().GetId ();
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
                if (_built) { throw new Exception ("Cant change built mask."); }
                if (Array.IndexOf (Include, poolId, 0, IncludeCount) != -1) { throw new Exception ($"{typeof (T).Name} already in constraints list."); }
                if (Array.IndexOf (Exclude, poolId, 0, ExcludeCount) != -1) { throw new Exception ($"{typeof (T).Name} already in constraints list."); }
#endif
                if (IncludeCount == Include.Length) { Array.Resize (ref Include, IncludeCount << 1); }
                Include[IncludeCount++] = poolId;
                return this;
            }

#if UNITY_2020_3_OR_NEWER
            [UnityEngine.Scripting.Preserve]
#endif
            [MethodImpl (MethodImplOptions.AggressiveInlining)]
            public Mask Exc<T> () where T : struct {
                var poolId = _world.GetPool<T> ().GetId ();
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
                if (_built) { throw new Exception ("Cant change built mask."); }
                if (Array.IndexOf (Include, poolId, 0, IncludeCount) != -1) { throw new Exception ($"{typeof (T).Name} already in constraints list."); }
                if (Array.IndexOf (Exclude, poolId, 0, ExcludeCount) != -1) { throw new Exception ($"{typeof (T).Name} already in constraints list."); }
#endif
                if (ExcludeCount == Exclude.Length) { Array.Resize (ref Exclude, ExcludeCount << 1); }
                Exclude[ExcludeCount++] = poolId;
                return this;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public EcsFilter<NoFilterData> End(int capacity = 512) {
                return End<NoFilterData>(capacity);
            }


            [MethodImpl (MethodImplOptions.AggressiveInlining)]
            public EcsFilter<T> End<T> (int capacity = 512) where T:IFilterData {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
                if (_built) { throw new Exception ("Cant change built mask."); }
                _built = true;
#endif
                Array.Sort (Include, 0, IncludeCount);
                Array.Sort (Exclude, 0, ExcludeCount);
                // calculate hash.
                Hash = IncludeCount + ExcludeCount;
                for (int i = 0, iMax = IncludeCount; i < iMax; i++) {
                    Hash = unchecked (Hash * 314159 + Include[i]);
                }
                for (int i = 0, iMax = ExcludeCount; i < iMax; i++) {
                    Hash = unchecked (Hash * 314159 - Exclude[i]);
                }
                var (filter, isNew) = _world.GetFilterInternal<T> (this, capacity);
                if (!isNew) { Recycle (); }
                return filter;
            }

            [MethodImpl (MethodImplOptions.AggressiveInlining)]
            void Recycle () {
                Reset ();
                if (_world._masksCount == _world._masks.Length) {
                    Array.Resize (ref _world._masks, _world._masksCount << 1);
                }
                _world._masks[_world._masksCount++] = this;
            }
        }

        public struct EntityData {
            public short Gen;
            public short ComponentsCount;
        }
    }

#if LEOECSLITE_WORLD_EVENTS
    public interface IEcsWorldEventListener {
        void OnEntityCreated (int entity);
        void OnEntityChanged (int entity);
        void OnEntityDestroyed (int entity);
        void OnFilterCreated (EcsFilter filter);
        void OnWorldResized (int newSize);
        void OnWorldDestroyed (EcsWorld world);
    }
#endif
}

#if ENABLE_IL2CPP
// Unity IL2CPP performance optimization attribute.
namespace Unity.IL2CPP.CompilerServices {
    enum Option {
        NullChecks = 1,
        ArrayBoundsChecks = 2
    }

    [AttributeUsage (AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    class Il2CppSetOptionAttribute : Attribute {
        public Option Option { get; private set; }
        public object Value { get; private set; }

        public Il2CppSetOptionAttribute (Option option, object value) { Option = option; Value = value; }
    }
}
#endif