// ----------------------------------------------------------------------------
// The MIT License
// Lightweight ECS framework https://github.com/Leopotam/ecslite
// Copyright (c) 2021-2022 Leopotam <leopotam@gmail.com>
// ----------------------------------------------------------------------------

//#define USE_FIXED_ARRAYS 

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;

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

		protected struct TaggedFilter {
			public Mask.BitMaskData filterBitMaskData;
			public EcsFilter filter;
		}

		public const int ENTITYDATA_AMOUNT_COMPONENT_BITMASKS = 2;

		public const int ENTITYID_MASK_ENTITY = 0b00000000001111111111111111111111;
		public const int ENTITYID_MASK_GEN = 0b00000011110000000000000000000000;
		public const int ENTITYID_MASK_WORLD = 0b01111100000000000000000000000000;
		public const int ENTITYID_SHIFT_GEN = 22;
		public const int ENTITYID_SHIFT_WORLD = 26;

		public const UInt32 TAGFILTERMASK_ENTITY_TYPE = 0b00000000000000000000000000001111;

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
				throw new Exception($"You added more worlds than supported({MAX_WORLDS})");
			}
			worlds[idx] = world;
		}

		public static void DestroyWorlds() {
			for (int i = 0, count = worlds.Length; i < count; i++) {
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

		protected int taggedFilterAmount = 0;
		protected TaggedFilter[] _filtersByTagMask;
		protected Mask[] _masks;
		protected int _masksCount;
		private Action<EcsWorld,bool, int, int> componentChangeCallback;
		private Action<EcsWorld,bool,bool, int> entityChangeCallback;
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
		readonly List<int> _leakedEntities = new List<int>(512);

		internal bool CheckForLeakedEntities() {
			if (_leakedEntities.Count > 0) {
				for (int i = 0, iMax = _leakedEntities.Count; i < iMax; i++) {
					ref var entityData = ref Entities[_leakedEntities[i]];
					if (entityData.Gen > 0 && !entityData.HasComponents) {
						return true;
					}
				}
				_leakedEntities.Clear();
			}
			return false;
		}
#endif

#if ECS_INT_PACKED
		public EcsWorld(int _worldIdx, in Config cfg = default) {
			RegisterWorlds(_worldIdx, this);
			worldIdx = _worldIdx;
			worldBitmask = _worldIdx << ENTITYID_SHIFT_WORLD;
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
			_poolHashes = new Dictionary<Type, IEcsPool>(capacity);
			_filtersByIncludedComponents = new List<EcsFilter>[capacity];
			_filtersByExcludedComponents = new List<EcsFilter>[capacity];
			_filtersByTagMask = new TaggedFilter[capacity];
			_poolDenseSize = cfg.PoolDenseSize > 0 ? cfg.PoolDenseSize : Config.PoolDenseSizeDefault;
			_poolRecycledSize = cfg.PoolRecycledSize > 0 ? cfg.PoolRecycledSize : Config.PoolRecycledSizeDefault;
			_poolsCount = 0;
			// filters.
			capacity = cfg.Filters > 0 ? cfg.Filters : Config.FiltersDefault;
			_hashedFilters = new Dictionary<int, EcsFilter>(capacity);
			_allFilters = new List<EcsFilter>(capacity);
			// masks.
			_masks = new Mask[64];
			_masksCount = 0;
#if LEOECSLITE_WORLD_EVENTS
			_eventListeners = new List<IEcsWorldEventListener> (4);
#endif
			_destroyed = false;
		}

		public void Destroy() {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (CheckForLeakedEntities()) { throw new Exception($"Empty entity detected before EcsWorld.Destroy()."); }
#endif
			_destroyed = true;
			for (var i = _entitiesCount - 1; i >= 0; i--) {
				ref var entityData = ref Entities[i];
				if (entityData.HasComponents) {
					DelEntity(i);
				}
			}
			_pools = Array.Empty<IEcsPool>();
			_poolHashes.Clear();
			_hashedFilters.Clear();
			_allFilters.Clear();
			_filtersByIncludedComponents = Array.Empty<List<EcsFilter>>();
			_filtersByExcludedComponents = Array.Empty<List<EcsFilter>>();
#if LEOECSLITE_WORLD_EVENTS
			for (var ii = _eventListeners.Count - 1; ii >= 0; ii--) {
				_eventListeners[ii].OnWorldDestroyed (this);
			}
#endif
			worlds[worldIdx] = null;
		}

		/// <summary>
		/// Callbacks to be informed if components or entities are created/deleted 
		/// </summary>
		/// <param name="componentChangeCallback"></param>
		public void SetChangeCallbacks(Action<EcsWorld,bool,bool, int> entityChangeCallback, Action<EcsWorld,bool,int,int> componentChangeCallback) {
			this.componentChangeCallback = componentChangeCallback;
			this.entityChangeCallback = entityChangeCallback;
		}

		/// <summary>
		/// Let the filterdata be updated to new memory addresses on next enumator-interation
		/// </summary>
		public void _MarkFiltersDirty() {
			for (int i = 0, iEnd = _allFilters.Count; i < iEnd; i++) {
				_allFilters[i].updateFilters = true;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsAlive() {
			return !_destroyed;
		}

		/// <summary>
		/// Return entityData to this entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		public ref EntityData GetEntityData(int packedEntity) {
			int rawEntity = GetPackedRawEntityId(packedEntity);
			return ref Entities[rawEntity];
		}

		public int NewEntity(uint entityType = 0) {
#if EZ_SANITY_CHECK
			// check if entityType is valid. if the entityType exceeds the possible range it would automatically
			// set tag-switches which should not be done.
			// At least not for now. It might have a usecase :thinking: 
			if ((entityType & ~TAGFILTERMASK_ENTITY_TYPE) > 0) {
				throw new Exception($"NewEntity: entityTypeId out of range! You are only allowed to use values from 0 to {TAGFILTERMASK_ENTITY_TYPE}");
			}
#endif
			int entity;
			uint gen = 0;
			if (_recycledEntitiesCount > 0) {
				entity = _recycledEntities[--_recycledEntitiesCount];
				ref var entityData = ref Entities[entity];
				entityData.ReactiveDestroyed();
				gen = entityData.Gen;
				// set the entityType as initial bitmask, only having the entityType and no tags attached
				entityData.tagBitMask = entityType;
			} else {
				// new entity.
				if (_entitiesCount == Entities.Length) {
					// resize entities and component pools.
					var newSize = _entitiesCount << 1;
					Array.Resize(ref Entities, newSize);
					for (int i = 0, iMax = _poolsCount; i < iMax; i++) {
						_pools[i].Resize(newSize);
					}
					for (int i = 0, iMax = _allFilters.Count; i < iMax; i++) {
						_allFilters[i].ResizeSparseIndex(newSize);
					}
#if LEOECSLITE_WORLD_EVENTS
					for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
						_eventListeners[ii].OnWorldResized (newSize);
					}
#endif
				}
				entity = _entitiesCount++;
				gen = 0; // we start with generation 0
						 // set the entityType as initial bitmask, only having the entityType and no tags attached
				Entities[entity].tagBitMask = entityType;
			}
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			_leakedEntities.Add(entity);
#endif
#if DEBUG && LEOECSLITE_WORLD_EVENTS
			for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
				_eventListeners[ii].OnEntityCreated (entity);
			}
#endif
#if ECS_INT_PACKED
			entity = PackEntity(entity, (int)gen);
#endif
			if (entityChangeCallback != null) {
				entityChangeCallback(this,false,true, entity); // tell callback this entity is being created
			}
			return entity;
		}


		/// <summary>
		/// Clear all tags from entity-statemask
		/// </summary>
		/// <param name="packedEntity"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void ClearEntityStateMask(int packedEntity) {
			int rawEntity = GetPackedRawEntityId(packedEntity);

			// work directly on the _bitmask-value
			uint newMask = Entities[rawEntity].tagBitMask;
			newMask &= TAGFILTERMASK_ENTITY_TYPE;
			OnEntityTagChangeInternal(packedEntity, newMask);

			// TODO: execute filters
		}


		/// <summary>
		/// Add bitmask (or operation) to entity's tagMask multiple 
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="setMask"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void AddMultiTagMask(int packedEntity, UInt32 setMask) {
#if EZ_SANITY_CHECK
			if ((setMask & TAGFILTERMASK_ENTITY_TYPE) > 0) {
				throw new Exception($"SetMask[entity:{packedEntity} mask:{setMask}]: Tried to set illegal setMask: mask would change entity-type!");
			}
#endif
			int rawEntity = GetPackedRawEntityId(packedEntity);

			uint newMask = Entities[rawEntity].tagBitMask;
			newMask |= setMask;

			OnEntityTagChangeInternal(packedEntity, newMask);
		}

		/// <summary>
		/// Add bitmask (or operation) to entity's tagMask multiple 
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1">tag to be set</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void AddTag(int packedEntity, UInt32 tag1) {
			// TODO: check that only sigle tag is used (no combination)

			// for now use the multiTag version
			AddMultiTagMask(packedEntity, tag1);
		}

		/// <summary>
		/// Add bitmask (or operation) to entity's tagMask multiple 
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1">tag to be set</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void AddTag(int packedEntity, UInt32 tag1, UInt32 tag2) {
			// TODO: check that only sigle tag is used (no combination)

			// for now use the multiTag version
			AddMultiTagMask(packedEntity, tag1 | tag2);
		}


		/// <summary>
		/// Add tag to entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1">tag to be set</param>
		/// <param name="tag2">tag to be set</param>
		/// <param name="tag3">tag to be set</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void AddTag(int packedEntity, UInt32 tag1, UInt32 tag2, UInt32 tag3) {
			// TODO: check that only sigle tag is used (no combination)

			// for now use the multiTag version
			AddMultiTagMask(packedEntity, tag1 | tag2 | tag3);
		}


		/// <summary>
		/// Get tagmask of specified entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public UInt32 GetTagMask(int packedEntity) {
			int rawEntity = GetPackedRawEntityId(packedEntity);
			return Entities[rawEntity].tagBitMask;
		}


		/// <summary>
		/// Check if entity has the specific tag-bitmask set (all bits needs to be set)
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="bitmask"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasTagAll(int packedEntity, UInt32 bitmask) {
			int rawEntity = GetPackedRawEntityId(packedEntity);
			return (Entities[rawEntity].tagBitMask & bitmask)==bitmask;
		}

		/// <summary>
		/// Check if entity at least one bit of the specific tag-bitmask set
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="bitmask"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasTagSome(int packedEntity, UInt32 bitmask) {
			int rawEntity = GetPackedRawEntityId(packedEntity);
			return (Entities[rawEntity].tagBitMask & bitmask) > 0;
		}


		/// <summary>
		/// Set specified mask as is. Only keep the entityType
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="setMask"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void SetTagMask(int packedEntity, UInt32 setMask) {
#if EZ_SANITY_CHECK
			if ((setMask & TAGFILTERMASK_ENTITY_TYPE) > 0) {
				throw new Exception($"SetMask.setMask[entity:{packedEntity} mask:{setMask}]: Tried to set illegal setMask: mask would change entity-type!");
			}
			if ((setMask & TAGFILTERMASK_ENTITY_TYPE) > 0) {
				throw new Exception($"SetMask.unsetMask[entity:{packedEntity} mask:{setMask}]: Tried to set illegal setMask: mask would change entity-type!");
			}
#endif
			int rawEntity = GetPackedRawEntityId(packedEntity);

			// use a clean-mask with only the entityType set
			uint mask = Entities[rawEntity].tagBitMask & TAGFILTERMASK_ENTITY_TYPE;
			mask |= setMask;

			OnEntityTagChangeInternal(packedEntity, mask);
		}


		/// <summary>
		/// Unset the specified bits from the entity's tagMask
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="unsetMask"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]/// 
		private void UnsetTagMask(int packedEntity, UInt32 unsetMask) {
#if EZ_SANITY_CHECK
			if ((unsetMask & TAGFILTERMASK_ENTITY_TYPE) > 0) {
				throw new Exception($"SetMask[entity:{packedEntity} mask:{unsetMask}]: Tried to set illegal setMask: mask would change entity-type!");
			}
#endif
			int rawEntity = GetPackedRawEntityId(packedEntity);

			// work directly on the _bitmask-value
			uint newMaks = Entities[rawEntity].tagBitMask;
			newMaks &= ~unsetMask;
			OnEntityTagChangeInternal(packedEntity, newMaks);
		}

		/// <summary>
		/// Remove tag from entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void RemoveTag(int packedEntity, UInt32 tag1) {
			// TODO: add check to make sure this is a single-tag

			// TODO: write optimizied code to only check filters that have this tag involved
			// for now using multicheck. 
			UnsetTagMask(packedEntity, tag1);
		}

		/// <summary>
		/// Remove tag from entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void RemoveTag(int packedEntity, UInt32 tag1, UInt32 tag2) {
			// TODO: add check to make sure this is a single-tag

			// TODO: write optimizied code to only check filters that have this tag involved
			// for now using multicheck. 
			UnsetTagMask(packedEntity, tag1 | tag2);
		}


		/// <summary>
		/// Remove tag from entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1"></param>
		/// <param name="tag2"></param>
		/// <param name="tag3"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void RemoveTag(int packedEntity, UInt32 tag1, UInt32 tag2, UInt32 tag3) {
			// TODO: add check to make sure this is a single-tag

			// TODO: write optimizied code to only check filters that have this tag involved
			// for now using multicheck. 
			UnsetTagMask(packedEntity, tag1 | tag2 | tag3);
		}


#if ECS_INT_PACKED
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int PackEntity(int plainEntity) {
			int gen = (int)GetEntityGen(plainEntity) << ENTITYID_SHIFT_GEN;
			int packedEntity = worldBitmask | gen | plainEntity;
			return packedEntity;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int PackEntity(int plainEntity, int gen) {
			gen = gen << ENTITYID_SHIFT_GEN;
			int packedEntity = worldBitmask | gen | plainEntity;
			return packedEntity;
		}

		/// <summary>
		/// Unpack and return values as ValueTuple
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static (int, int, uint) UnpackEntity(int packedEntity) {
			// due to better inlining, dont using the specialized methods here. 
			int rawEntity = packedEntity & ENTITYID_MASK_ENTITY;
			int worldID = (packedEntity & ENTITYID_MASK_WORLD) >> ENTITYID_SHIFT_WORLD;
			uint gen = (uint)(packedEntity & ENTITYID_MASK_GEN) >> ENTITYID_SHIFT_GEN;
#if EZ_SANITY_CHECK
			// check if generation of the packed entity fit with the generation of this entity in the ecs.
			// If there is a mismatch this means that we stored a destroyed entity somewhere!
			uint ecsGen = EcsWorld.worlds[worldID].GetEntityGen(packedEntity);
			if (gen != ecsGen) {
				throw new Exception($"PackedEntity[{packedEntity}]: There is a generation mismatch![packed:{ecsGen} current:{gen}] Seems we stored a destroyed Entity somewhere!");
			}
#endif
			return (rawEntity, worldID, gen);
		}

		/// <summary>
		/// Unpack and return values as ValueTuple
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static (int, T, uint) UnpackEntityWithWorld<T>(int packedEntity) where T : EcsWorld {
			// due to better inlining, dont using the specialized methods here. 
			int rawEntity = packedEntity & ENTITYID_MASK_ENTITY;
			int worldID = (packedEntity & ENTITYID_MASK_WORLD) >> ENTITYID_SHIFT_WORLD;
			EcsWorld world = worlds[worldID];
			uint gen = (uint)(packedEntity & ENTITYID_MASK_GEN) >> ENTITYID_SHIFT_GEN;
#if EZ_SANITY_CHECK
			// check if generation of the packed entity fit with the generation of this entity in the ecs.
			// If there is a mismatch this means that we stored a destroyed entity somewhere!
			uint ecsGen = EcsWorld.worlds[worldID].GetEntityGen(packedEntity);
			if (gen != ecsGen) {
				throw new Exception($"PackedEntity[{packedEntity}]: There is a generation mismatch![packed:{ecsGen} current:{gen}] Seems we stored a destroyed Entity somewhere!");
			}
#endif
			return (rawEntity, UnsafeUtility.As<EcsWorld, T>(ref world), gen);
			//return (rawEntity, (T)world, gen);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static uint GetPackedGen(int packedEntity) {
			uint gen = (uint)(packedEntity & ENTITYID_MASK_GEN) >> ENTITYID_SHIFT_GEN;
			return gen;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int GetPackedWorldID(int packedEntity) {
			int worldId = (packedEntity & ENTITYID_MASK_WORLD) >> ENTITYID_SHIFT_WORLD;
			return worldId;
		}

		/// <summary>
		/// Get packed EcsWorld as specicalized EcsWorld-Type
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static T GetPackedWorld<T>(int packedEntity) where T : EcsWorld {
			int worldId = (packedEntity & ENTITYID_MASK_WORLD) >> ENTITYID_SHIFT_WORLD;
			T world = UnsafeUtility.As<EcsWorld, T>(ref worlds[worldId]);
#if EZ_SANITY_CHECK
			// check if generation of the packed entity fit with the generation of this entity in the ecs.
			// If there is a mismatch this means that we stored a destroyed entity somewhere!
			uint ecsGen = world.GetEntityGen(packedEntity);
			uint packedGen = EcsWorld.GetPackedGen(packedEntity);
			if (ecsGen != packedGen) {
				throw new Exception($"PackedEntity[{packedEntity}]: There is a generation mismatch![packed:{packedGen} current:{ecsGen}] Seems we stored a destroyed Entity somewhere!");
			}
#endif
			//T world = (T)worlds[worldId];
			return world;
		}


		/// <summary>
		/// Get packed world as base-class EcsWorld
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref EcsWorld GetPackedWorld(int packedEntity) {
			int worldId = (packedEntity & ENTITYID_MASK_WORLD) >> ENTITYID_SHIFT_WORLD;
			ref EcsWorld world = ref worlds[worldId];
#if EZ_SANITY_CHECK
			// check if generation of the packed entity fit with the generation of this entity in the ecs.
			// If there is a mismatch this means that we stored a destroyed entity somewhere!
			uint ecsGen = world.GetEntityGen(packedEntity);
			uint packedGen = EcsWorld.GetPackedGen(packedEntity);
			if (ecsGen != packedGen) {
				throw new Exception($"PackedEntity[{packedEntity}]: There is a generation mismatch![packed:{packedGen} current:{ecsGen}] Seems we stored a destroyed Entity somewhere!");
			}
#endif
			return ref world;
		}



		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int GetPackedRawEntityId(int packedEntity) {
			var rawEntity = packedEntity & ENTITYID_MASK_ENTITY;
			return rawEntity;
		}
#endif

		public void DelEntity(int entity) {
#if ECS_INT_PACKED
			entity = EcsWorld.GetPackedRawEntityId(entity);
#endif
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (entity < 0 || entity >= _entitiesCount) { throw new Exception("Cant touch destroyed entity."); }
#endif
			ref var entityData = ref Entities[entity];
#if EZ_SANITY_CHECK
			if (entityData.Destroyed) {
				throw new Exception("Tried to destroy already destroyed entity");
			}
#endif
			// kill components.
			if (entityData.HasComponents) {
				if (entityChangeCallback != null) {
					entityChangeCallback(this,true,false, entity); // tell callback this entity is going to be destroyed 
				}
				var idx = 0;
				while (entityData.HasComponents && idx < _poolsCount) {
					for (; idx < _poolsCount; idx++) {
						if (_pools[idx].Has(entity)) {
							_pools[idx++].Del(entity);
							break;
						}
					}
				}
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
				if (entityData.HasComponents) {
					// TODO: show component bitmasks
					throw new Exception($"Invalid components count on entity {entity} still has components!");
				}
#endif
				return;
			}

			ClearEntityStateMask(entity);
			//entityData.Gen = (uint)(entityData.Gen == short.MaxValue ? -1 : -(entityData.Gen + 1)); // no need to check for end of short. This is done on Gen-Property 
			// entityData.Gen = (uint)(-(entityData.Gen + 1));
			entityData.Destroy();

			if (_recycledEntitiesCount == _recycledEntities.Length) {
				Array.Resize(ref _recycledEntities, _recycledEntitiesCount << 1);
			}
			_recycledEntities[_recycledEntitiesCount++] = entity;
#if DEBUG && LEOECSLITE_WORLD_EVENTS
			for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
				_eventListeners[ii].OnEntityDestroyed (entity);
			}
#endif
			if (entityChangeCallback != null) {
				entityChangeCallback(this,false, false, entity); // tell callback this entity is destroyed for good
			}
		}

		//		[MethodImpl (MethodImplOptions.AggressiveInlining)]
		//		public int GetComponentsCount (int entity) {
		//#if ECS_INT_PACKED
		//			entity = EcsWorld.GetPackedRawEntityId(entity);
		//#endif
		//			return Entities[entity].ComponentsCount;
		//		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint GetEntityGen(int entity) {
#if ECS_INT_PACKED
			entity = EcsWorld.GetPackedRawEntityId(entity);
#endif
			return Entities[entity].Gen;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int GetAllocatedEntitiesCount() {
			return _entitiesCount;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int GetWorldSize() {
			return Entities.Length;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public EntityData[] GetRawEntities() {
			return Entities;
		}

		public EcsPool<T> GetPool<T>(int initialDenseSize = -1) where T : struct {
			var poolType = typeof(T);
			if (_poolHashes.TryGetValue(poolType, out var rawPool)) {
				return (EcsPool<T>)rawPool;
			}
			int initalPoolDenseSize = initialDenseSize != -1 ? initialDenseSize : _poolDenseSize;
			var pool = new EcsPool<T>(this, _poolsCount, initalPoolDenseSize, Entities.Length, _poolRecycledSize);
			_poolHashes[poolType] = pool;
			if (_poolsCount == _pools.Length) {
				var newSize = _poolsCount << 1;
				Array.Resize(ref _pools, newSize);
				Array.Resize(ref _filtersByIncludedComponents, newSize);
				Array.Resize(ref _filtersByExcludedComponents, newSize);
			}
			_pools[_poolsCount++] = pool;
			return pool;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public IEcsPool GetPoolById(int typeId) {
			return typeId >= 0 && typeId < _poolsCount ? _pools[typeId] : null;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public IEcsPool GetPoolByType(Type type) {
			return _poolHashes.TryGetValue(type, out var pool) ? pool : null;
		}

		public int GetAllEntities(ref int[] entities) {
			var count = _entitiesCount - _recycledEntitiesCount;
			if (entities == null || entities.Length < count) {
				entities = new int[count];
			}
			var id = 0;
			for (int i = 0, iMax = _entitiesCount; i < iMax; i++) {
				ref var entityData = ref Entities[i];
				// should we skip empty entities here?
				if (!entityData.Destroyed) {
					entities[id++] = i;
				}
			}
			return count;
		}

		public int GetAllPools(ref IEcsPool[] pools) {
			var count = _poolsCount;
			if (pools == null || pools.Length < count) {
				pools = new IEcsPool[count];
			}
			Array.Copy(_pools, 0, pools, 0, _poolsCount);
			return _poolsCount;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Mask Filter<T>() where T : struct {
			var mask = _masksCount > 0 ? _masks[--_masksCount] : new Mask(this);
			return mask.Inc<T>();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Mask Filter(uint tagsSet) {
			var mask = _masksCount > 0 ? _masks[--_masksCount] : new Mask(this);
			mask.TagsSet(tagsSet);
			return mask;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Mask Filter(uint tagsSet, uint tagsNotSet) {
			var mask = _masksCount > 0 ? _masks[--_masksCount] : new Mask(this);
			mask.TagsSet(tagsSet);
			mask.TagsNotSet(tagsNotSet);
			return mask;
		}


		public int GetComponents(int entity, ref object[] list) {
#if ECS_INT_PACKED
			entity = EcsWorld.GetPackedRawEntityId(entity);
#endif
			// TODO: Do I want to have a component-count equivalent? Not,yet
			//var itemsCount = Entities[entity].ComponentsCount;
			//if (itemsCount == 0) { return 0; }
			int itemsCount = 0;
			if (list == null || list.Length < _pools.Length) {
				list = new object[_pools.Length];
			}
			for (int i = 0, j = 0, iMax = _poolsCount; i < iMax; i++) {
				if (_pools[i].Has(entity)) {
					list[j++] = _pools[i].GetRaw(entity);
					itemsCount++;
				}
			}
			return itemsCount;
		}

		public object[] GetComponents(int entity) {
			object[] comps = null;
			GetComponents(entity, ref comps);
			return comps;
		}

		public int GetComponentTypes(int entity, ref Type[] list) {
#if ECS_INT_PACKED
			entity = EcsWorld.GetPackedRawEntityId(entity);
#endif
			int itemsCount = 0;
			if (list == null || list.Length < _pools.Length) {
				list = new Type[_pools.Length];
			}
			for (int i = 0, j = 0, iMax = _poolsCount; i < iMax; i++) {
				if (_pools[i].Has(entity)) {
					list[j++] = _pools[i].GetComponentType();
					itemsCount++;
				}
			}
			return itemsCount;
		}


		/// <summary>
		/// Needs to be called with unpacked entity 
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsEntityAliveInternal(int entity) {
			return entity >= 0 && entity < _entitiesCount && !Entities[entity].Destroyed;
		}

		(EcsFilter<T>, bool) GetFilterInternal<T>(Mask mask, int capacity = 16) where T : IFilterData {
			var hash = mask.Hash;
			var exists = _hashedFilters.TryGetValue(hash, out var filter);
			if (exists) {
				// reuse!
				return ((EcsFilter<T>)filter, false);
			}
			filter = new EcsFilter<T>(this, mask, capacity, Entities.Length);
			_hashedFilters[hash] = filter;
			_allFilters.Add(filter);

			// add filter to tagMask-lookup
			UInt32 tagmaskHash = mask.TagMaskHash;
			if (tagmaskHash != 0) {
				_filtersByTagMask[taggedFilterAmount] = new TaggedFilter() {
					filterBitMaskData = mask.bitmaskData,
					filter = filter
				};

				taggedFilterAmount++;
				if (taggedFilterAmount == _filtersByTagMask.Length) {
					Array.Resize(ref _filtersByTagMask, _filtersByTagMask.Length << 1);
				}
			}

			// add to component dictionaries for fast compatibility scan.
			for (int i = 0, iMax = mask.IncludeCount; i < iMax; i++) {
				var list = _filtersByIncludedComponents[mask.Include[i]];
				if (list == null) {
					list = new List<EcsFilter>(8);
					_filtersByIncludedComponents[mask.Include[i]] = list;
				}
				list.Add(filter);
			}
			for (int i = 0, iMax = mask.ExcludeCount; i < iMax; i++) {
				var list = _filtersByExcludedComponents[mask.Exclude[i]];
				if (list == null) {
					list = new List<EcsFilter>(8);
					_filtersByExcludedComponents[mask.Exclude[i]] = list;
				}
				list.Add(filter);
			}
			// scan exist entities for compatibility with new filter.
			for (int i = 0, iMax = _entitiesCount; i < iMax; i++) {
				ref var entityData = ref Entities[i];
				if (entityData.HasComponents && IsMaskCompatible(ref mask.bitmaskData, i, entityData.tagBitMask)) {
					filter.AddEntity(i);
				}
			}
#if LEOECSLITE_WORLD_EVENTS
			for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
				_eventListeners[ii].OnFilterCreated (filter);
			}
#endif
			return ((EcsFilter<T>)filter, true);
		}


		private List<EcsFilter> removeFromFilter = new List<EcsFilter>();
		private List<EcsFilter> addToFilter = new List<EcsFilter>();


		/// <summary>
		/// reorganize filters if entities tags changed. This reacts works also on multiple tag changes
		/// </summary>
		/// <param name="entity"></param>
		/// <param name="newMask"></param>
		private void OnEntityTagChangeInternal(int entity, UInt32 newMask) {

			ref EntityData entityData = ref Entities[entity];
			UInt32 oldBitmask = entityData.tagBitMask;
			if (oldBitmask == newMask) {
				// nothing to change
				return;
			}

			removeFromFilter.Clear();
			addToFilter.Clear();

			// check filters from which we potentially need to remove this entity
			for (int i = 0; i < taggedFilterAmount; i++) {
				ref TaggedFilter filterData = ref _filtersByTagMask[i];

				bool maskCompatible = IsMaskCompatible(ref filterData.filterBitMaskData, entity, oldBitmask);
				if (maskCompatible) {
					// this entity is in this filter!
					// what we know:
					// - all component include and exclude fit
					// - all tags set/unset fit
					// - component-checks are still valid after tagMask change
					// - we need to check if the newMask makes the filter's tagMask invalid and remove if yes


					// check if new mask is compatible
					if (!IsTagsMaskCompatible(ref _filtersByTagMask[i].filterBitMaskData, newMask)) {
						removeFromFilter.Add(filterData.filter);
					}
				} else {
					// this entity is not (yet) in this filter
					// check if the new mask would apply
					bool newMaskCompatible = IsMaskCompatible(ref filterData.filterBitMaskData, entity, newMask);
					if (newMaskCompatible) {
						addToFilter.Add(filterData.filter);
					}
				}
			}

			// now execute removals
			for (int i = 0, iEnd = removeFromFilter.Count; i < iEnd; i++) {
				removeFromFilter[i].RemoveEntity(entity);
			}
			// change entity to new tagMask
			entityData.tagBitMask = newMask;
			// now add
			for (int i = 0, iEnd = addToFilter.Count; i < iEnd; i++) {
				addToFilter[i].AddEntity(entity);
			}
		}

		/// <summary>
		/// Needs to be called with unpacked entity 
		/// </summary>
		public void OnEntityChangeInternal(int entity, int componentType, bool added) {
			var includeList = _filtersByIncludedComponents[componentType];
			var excludeList = _filtersByExcludedComponents[componentType];

			if (added) {
				// add component.
				if (includeList != null) {
					foreach (var filter in includeList) {
						if (IsMaskCompatible(ref filter.GetMask().bitmaskData, entity, Entities[entity].tagBitMask)) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
							if (filter.SparseEntities[entity] > 0) { throw new Exception("Entity already in filter."); }
#endif
							filter.AddEntity(entity);
						}
					}
				}
				if (excludeList != null) {
					foreach (var filter in excludeList) {
						if (!IsMaskCompatible(ref filter.GetMask().bitmaskData, entity, Entities[entity].tagBitMask)) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
							if (filter.SparseEntities[entity] == 0) { throw new Exception("Entity not in filter."); }
#endif
							filter.RemoveEntity(entity);
						}
					}
				}
			} else {
				// remove component.
				if (includeList != null) {
					foreach (var filter in includeList) {
						if (!IsMaskCompatible(ref filter.GetMask().bitmaskData, entity, Entities[entity].tagBitMask)) {
							if (filter.SparseEntities[entity] == 0) {
								continue;
							}
							filter.RemoveEntity(entity);
						}
					}
				}
				if (excludeList != null) {
					foreach (var filter in excludeList) {
						if (IsMaskCompatible(ref filter.GetMask().bitmaskData, entity, Entities[entity].tagBitMask)) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
							if (filter.SparseEntities[entity] > 0) { throw new Exception("Entity already in filter."); }
#endif
							filter.AddEntity(entity);
						}
					}
				}
			}
			if (componentChangeCallback != null) {
				componentChangeCallback(this,added, entity, componentType);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		bool IsTagsMaskCompatible(ref Mask.BitMaskData filterBitmaskData, uint entityTagMask) {
			bool tagSetApplies;
			bool tagUnsetApplies;
			tagSetApplies = filterBitmaskData.tagMaskSet == 0 || (entityTagMask & filterBitmaskData.tagMaskSet) == filterBitmaskData.tagMaskSet;
			tagUnsetApplies = filterBitmaskData.tagMaskNotSet == 0 || (~entityTagMask & filterBitmaskData.tagMaskNotSet) != 0;
			return tagSetApplies && tagUnsetApplies;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		bool IsMaskCompatible(ref Mask.BitMaskData filterBitmaskData, int entity, uint entityTagBitMask) {
			ref EntityData entityData = ref Entities[entity];

			bool includeComponentsApplies;
			bool excludeComponentsApplies;

			unsafe {
#if EZ_SANITY_CHECK
				if (ENTITYDATA_AMOUNT_COMPONENT_BITMASKS != 2) {
					throw new Exception("Mask check is only supported for 2 long-bitmasks! Modify check to fit to more or less! This is hardcoded due to performance!");
				}
#endif


				bool tagsCompatible = IsTagsMaskCompatible(ref filterBitmaskData, entityTagBitMask);

				// remark: componentMasks: 0+1=includeComponentMasks 2+3=excludeComponentMasks

				includeComponentsApplies = (entityData.componentsBitMask[0] & filterBitmaskData.componentMasks[0]) == filterBitmaskData.componentMasks[0]
								&& (entityData.componentsBitMask[1] & filterBitmaskData.componentMasks[1]) == filterBitmaskData.componentMasks[1];

				excludeComponentsApplies = (entityData.componentsBitMask[0] & filterBitmaskData.componentMasks[2]) == 0
								&& (entityData.componentsBitMask[1] & filterBitmaskData.componentMasks[3]) == 0;

				//TODO: once we are sure this is working do merge all checks to one so that it stops checking on first fail!
				return includeComponentsApplies && excludeComponentsApplies && tagsCompatible;
			}

			//for (int i = 0, iMax = filterMask.IncludeCount; i < iMax; i++) {
			//	if (!_pools[filterMask.Include[i]].Has (entity)) {
			//		return false;
			//	}
			//}
			//for (int i = 0, iMax = filterMask.ExcludeCount; i < iMax; i++) {
			//	if (_pools[filterMask.Exclude[i]].Has (entity)) {
			//		return false;
			//	}
			//}
			//return true;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		bool IsMaskCompatibleWithout(Mask filterMask, int entity, int componentId) {
			for (int i = 0, iMax = filterMask.IncludeCount; i < iMax; i++) {
				var typeId = filterMask.Include[i];
				if (typeId == componentId || !_pools[typeId].Has(entity)) {
					return false;
				}
			}
			for (int i = 0, iMax = filterMask.ExcludeCount; i < iMax; i++) {
				var typeId = filterMask.Exclude[i];
				if (typeId != componentId && _pools[typeId].Has(entity)) {
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
			public struct BitMaskData {
				internal UInt32 tagMaskSet;
				internal UInt32 tagMaskNotSet;
				internal UInt64[] componentMasks; // TODO make it fixed
			}
			readonly EcsWorld _world;
			internal int[] Include;
			internal int[] Exclude;
			internal int IncludeCount;
			internal int ExcludeCount;
			internal int Hash;
			internal BitMaskData bitmaskData;

#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			bool _built;
#endif

			internal Mask(EcsWorld world) {
				_world = world;
				Include = new int[8];
				Exclude = new int[2];
				bitmaskData.componentMasks = new UInt64[ENTITYDATA_AMOUNT_COMPONENT_BITMASKS * 2];
				Reset();
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			void Reset() {
				IncludeCount = 0;
				ExcludeCount = 0;
				Hash = 0;
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
				_built = false;
#endif
			}

			public UInt32 TagMaskHash => (bitmaskData.tagMaskSet << 32) + bitmaskData.tagMaskNotSet;

			/// <summary>
			/// Tag-bits that needs to be set to be a valid filter 
			/// </summary>
			/// <param name="bitmask"></param>
			/// <returns></returns>
			public Mask TagsSet(UInt32 bitmask) {
				bitmaskData.tagMaskSet = bitmask;
				return this;
			}

			/// <summary>
			/// Tag-bits that must not be set to be a valid filter
			/// </summary>
			/// <param name="bitmask"></param>
			/// <returns></returns>
			public Mask TagsNotSet(UInt32 bitmask) {
				bitmaskData.tagMaskNotSet = bitmask;
				return this;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public Mask Inc<T>() where T : struct {
				var pool = _world.GetPool<T>();
				var poolId = pool.GetId();
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
				if (_built) { throw new Exception("Cant change built mask."); }
				if (Array.IndexOf(Include, poolId, 0, IncludeCount) != -1) { throw new Exception($"{typeof(T).Name} already in constraints list."); }
				if (Array.IndexOf(Exclude, poolId, 0, ExcludeCount) != -1) { throw new Exception($"{typeof(T).Name} already in constraints list."); }
#endif
				if (IncludeCount == Include.Length) {
					Array.Resize(ref Include, IncludeCount << 1);
				}
				Include[IncludeCount++] = poolId; // TODO: Include[] any use cases left? Keep it for now...
				bitmaskData.componentMasks[pool._bitmaskFieldId] |= pool._componentBitmask;
				return this;
			}

#if UNITY_2020_3_OR_NEWER
			[UnityEngine.Scripting.Preserve]
#endif
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public Mask Exc<T>() where T : struct {
				var pool = _world.GetPool<T>();
				var poolId = pool.GetId();

#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
				if (_built) { throw new Exception("Cant change built mask."); }
				if (Array.IndexOf(Include, poolId, 0, IncludeCount) != -1) { throw new Exception($"{typeof(T).Name} already in constraints list."); }
				if (Array.IndexOf(Exclude, poolId, 0, ExcludeCount) != -1) { throw new Exception($"{typeof(T).Name} already in constraints list."); }
#endif
				if (ExcludeCount == Exclude.Length) {
					Array.Resize(ref Exclude, ExcludeCount << 1);
				}
				Exclude[ExcludeCount++] = poolId; // TODO: exclude deprecated. Any usecase left? Let's keep it for now
				bitmaskData.componentMasks[pool._bitmaskFieldId + ENTITYDATA_AMOUNT_COMPONENT_BITMASKS] |= pool._componentBitmask;
				return this;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public EcsFilter<NoFilterData> End(int capacity = 512) {
				return End<NoFilterData>(capacity);
			}


			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public EcsFilter<T> End<T>(int capacity = 512) where T : IFilterData {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
				if (_built) { throw new Exception("Cant change built mask."); }
				_built = true;
#endif
				Array.Sort(Include, 0, IncludeCount);
				Array.Sort(Exclude, 0, ExcludeCount);
				// calculate hash.
				Hash = IncludeCount + ExcludeCount;

				// take tagMasks into account for hash-calculation
				Hash = unchecked(Hash * 314159 + (int)bitmaskData.tagMaskNotSet);
				Hash = unchecked(Hash * 314159 + (int)bitmaskData.tagMaskSet);

				for (int i = 0, iMax = IncludeCount; i < iMax; i++) {
					Hash = unchecked(Hash * 314159 + Include[i]);
				}
				for (int i = 0, iMax = ExcludeCount; i < iMax; i++) {
					Hash = unchecked(Hash * 314159 - Exclude[i]);
				}
				var (filter, isNew) = _world.GetFilterInternal<T>(this, capacity);
				if (!isNew) { Recycle(); }
				return filter;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			void Recycle() {
				Reset();
				if (_world._masksCount == _world._masks.Length) {
					Array.Resize(ref _world._masks, _world._masksCount << 1);
				}
				_world._masks[_world._masksCount++] = this;
			}
		}


		public unsafe struct EntityData {
			public const uint MASK_GEN = 0b0000000000000000000000001111;
			public const uint MASK_HAS_COMPONENTS = 0b0000000000000000000000010000;
			public const uint MASK_DESTROYED = 0b0000000000000000000000100000;
			// bit 01-04 gen
			// bit 05    has components
			// bit 06-32 unused
			public UInt32 entityInfo;

			/// <summary>
			/// Bitmask representing somekind of state of that entity. DO NOT SET VALUE DIRECTLY!!  THE EcsWorld needs to know if data changed here!!! As long as you know what you are doing....don't do it!
			/// Use .... not sure what, yet.  
			/// </summary>
			// bit 01-04 entity-type (e.g. settler, plant, ... 0=custom for entities, that are more like an helper entity...  there shouldn't be too much real entitiy-types. I hope 16 will be enough
			// bit 05-09 default-tags (tags that makes sense on any entity-type e.g. active,damaged?....
			// bit 10-32 custom-tags (entity-type specific tags)
			public UInt32 tagBitMask;

			/// <summary>
			/// Two 64bit longs to check for 128 components set to this entity
			/// </summary>
			[UnityEngine.Tooltip("Two 64bit longs to check for 128 components set to this entity")]

#if !USE_FIXED_ARRAYS
			public UInt64[] componentsBitMask;
#else
			[MarshalAs(UnmanagedType.ByValArray/*, SizeConst = 123*/)]
			public fixed UInt64 componentsBitMask[EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS];
#endif


			public void Destroy() {
#if EZ_SANITY_CHECK
				if (Destroyed) {
					throw new Exception($"Tried to destroy an already as destroyed marked entity");
				}
#endif
				// set destroyed bit
				entityInfo |= MASK_DESTROYED;
				// create gen to make sure it gets invalidated for packed-entities
				// make sure you stay in gen-number-range by &-MASK_GEN
				uint newGen = (Gen + 1) & MASK_GEN;

				entityInfo &= ~(MASK_GEN); // clear GEN-bits
				entityInfo |= newGen;      // set value
			}

			public void ReactiveDestroyed() {
#if EZ_SANITY_CHECK
				if (!Destroyed) {
					throw new Exception("Tried to activate an entity that is not marked as destroyed");
				}
#endif
				entityInfo &= ~MASK_DESTROYED;
				// no need to set a new generation as this is done by the destruction
			}


			public uint Gen => (entityInfo & MASK_GEN);

			public bool HasComponents => (entityInfo & MASK_HAS_COMPONENTS) > 0;

			public bool Destroyed => (entityInfo & MASK_DESTROYED) > 0;

			/// <summary>
			/// TagMaskFilterKey used to lookup corresponding EcsFilters that fit to this tagBitMask
			/// </summary>
			[UnityEngine.Tooltip("TagMaskFilterKey used to lookup corresponding EcsFilters that fit to this tagBitMask")]
			public UInt32 TagMaskFilterKey => (tagBitMask << 32) + (~tagBitMask);

			/// <summary>
			/// Get entity-type (stored in the tagBitMask)
			/// </summary>
			[UnityEngine.Tooltip("Get entity-type (stored in the tagBitMask)")]
			public uint EntityType => tagBitMask & TAGFILTERMASK_ENTITY_TYPE;

			public void _UpdateHasComponents() {
#if !USE_FIXED_ARRAYS
				if (componentsBitMask == null) {
					componentsBitMask = new UInt64[EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS];
				}
#endif
				for (int i = 0; i < EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS; i++) {
					if (componentsBitMask[i] > 0) {
						entityInfo |= MASK_HAS_COMPONENTS;
						return;
					}
				}
				entityInfo &= ~MASK_HAS_COMPONENTS;
			}


			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void SetComponentBit(int componentBitmaskId, uint setMask) {
#if !USE_FIXED_ARRAYS
				if (componentsBitMask == null) {
					componentsBitMask = new UInt64[EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS];
				}
#endif
				componentsBitMask[componentBitmaskId] |= setMask;
				_UpdateHasComponents();
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void UnsetComponentBit(int componentBitmaskId, uint unsetMask) {
#if !USE_FIXED_ARRAYS
				if (componentsBitMask == null) {
					componentsBitMask = new UInt64[EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS];
				}
#endif
				componentsBitMask[componentBitmaskId] &= ~unsetMask;
				_UpdateHasComponents();
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public bool CheckSingleComponent(int bitmaskIdx, uint bitmask) {
				return (componentsBitMask[bitmaskIdx] & bitmask) == bitmask;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public bool HasTagMask(uint tagmask) {
				return (tagBitMask & tagmask) == tagmask;
			}


			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public bool IsTagsMaskCompatible(ref Mask.BitMaskData filterBitmaskData, uint entityTagMask) {
				bool tagSetApplies = filterBitmaskData.tagMaskSet == 0 || (entityTagMask & filterBitmaskData.tagMaskSet) == filterBitmaskData.tagMaskSet;
				bool tagUnsetApplies = filterBitmaskData.tagMaskNotSet == 0 || (~entityTagMask & filterBitmaskData.tagMaskNotSet) != 0;
				return tagSetApplies && tagUnsetApplies;
			}
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