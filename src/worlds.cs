// ----------------------------------------------------------------------------
// The MIT License
// Lightweight ECS framework https://github.com/Leopotam/ecslite
// Copyright (c) 2021-2022 Leopotam <leopotam@gmail.com>
// ----------------------------------------------------------------------------

#define USE_FIXED_ARRAYS 

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Assertions;
using static Leopotam.EcsLite.EcsWorld.EntityData;

#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif


namespace Leopotam.EcsLite {
#if ENABLE_IL2CPP
	[Il2CppSetOption (Option.NullChecks, false)]
	[Il2CppSetOption (Option.ArrayBoundsChecks, false)]
#endif
	
	
	/// <summary>
	/// Interface to decorate components that should get their entity injected!
	/// CAUTIONS: IT IS VERY IMPORTANT THAT THOSE structs have their very first is the entity-field of type int
	/// e.g. 
	/// struct Component : IEntity {
	///   private int entity;
	///   public int Entity => entity;
	/// }
	/// </summary>
	public interface IEntity {
		public int Entity { get; }
	}

	public struct EntityHeader {
		public int entity;
	}

	
	public static class HashFunction {
		private static ulong[] primeNumbers = { 17, 19, 23 };

		public static ulong GetHash(ulong value1, ulong value2, ulong value3) {
			ulong hash = primeNumbers[0] * value1;
			hash ^= primeNumbers[1] * value2;
			hash ^= primeNumbers[2] * value3;

			return hash;
		}
	}

	public interface IEcsWorld { }

	public partial class EcsWorld : IEcsWorld {
#if ECS_INT_PACKED

		protected struct TaggedFilter {
			public Mask.BitMaskData filterBitMaskData;
			public EcsFilter filter;
		}

		/// <summary>
		/// TAG-Mask for special usage (not tags, but e.g. entity-type)
		/// </summary>
		public const UInt64 MASK_TAG_ENTITY_TYPE = 0b1111;

		/// <summary>
		/// TAG-bitmask to be usable to store tag-data
		/// </summary>
		public const UInt64 MASK_TAG_ENTITY_TYPE_INV = ~MASK_TAG_ENTITY_TYPE;

		public const int ENTITYDATA_AMOUNT_COMPONENT_BITMASKS = 2;

		public const int ENTITYID_SHIFT_GEN = 19;
		public const int ENTITYID_SHIFT_WORLD = 26;

		public const int ENTITYID_MASK_ENTITY = 0b1111111111111111111; // 19 bit raw entity ids
		public const int ENTITYID_MASK_GEN = (0b1111111) << ENTITYID_SHIFT_GEN;
		public const int ENTITYID_MASK_WORLD = (0b11111) << ENTITYID_SHIFT_WORLD;

		public const UInt64 TAGFILTERMASK_ENTITY_TYPE = 0b1111;

		public const int MAX_WORLDS = (1 << 5) - 1;
		public const int MAX_GEN = (1 << 7) - 1;
		public const int MAX_ENTITIES = (1 << 19) - 1;

		/// <summary>
		/// Amount of recycled entities that needs to be in the RecycledQueue before the system is
		/// actually using those to prevent a single entity to get potentially stressed by newEntity/DelEntity iterations
		/// </summary>
		public const int MIN_RECYCLED_TO_RECYCLE = 10; 

		public static EcsWorld[] worlds = new EcsWorld[MAX_WORLDS];

		
		/// <summary>
		/// Clear ECSWorlds! NEVER USE THIS! This is only useful for the TestRunner to clear worlds that were not removed
		/// </summary>
		public static void __ClearECSWorlds() {
			worlds = new EcsWorld[MAX_WORLDS];
		}

		/// <summary>
		/// Register this world with its specific idx
		/// </summary>
		/// <param name="idx"></param>
		/// <param name="world"></param>
		protected static void RegisterWorlds(int idx, EcsWorld world) {
			Assert.IsTrue(idx < MAX_WORLDS/*, $"You added more worlds than supported({MAX_WORLDS})"*/);
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
		public int WorldArrayIdx => _worldIdx - 1;
		/// <summary>
		/// dont use! Use Property WorldIdx
		/// </summary>
		[UnityEngine.Tooltip("dont use! Use Property WorldIdx")]
		public int _worldIdx;

		public int _EntitiesCount => _entitiesCount - _recycledEntities.Count;
		public int _RecycledEntitiesCount => _recycledEntities.Count;


#endif
		public EntityData[] Entities;
		public int _entitiesCount;
		public Queue<int> _recycledEntities;
		public int _recycledEntitiesCount;
		public IEcsPool[] _pools;
		public int _poolsCount;
		protected readonly int _poolDenseSize;
		protected readonly int _poolRecycledSize;
		protected readonly Dictionary<Type, IEcsPool> _poolHashes;
		protected readonly Dictionary<long, EcsFilter> _hashedFilters;
		protected readonly List<EcsFilter> _allFilters;

		protected List<EcsFilter>[] _filtersByIncludedComponents;
		protected List<EcsFilter>[] _filtersByExcludedComponents;

		protected int taggedFilterAmount = 0;
		protected TaggedFilter[] _filtersByTagMask;
		protected Mask[] _masks;
		protected int _masksCount;
		private Action<EcsWorld,bool, int, int> componentChangeCallback;
		private Action<EcsWorld,bool,bool, int> entityChangeCallback;
		private Action<EcsWorld, int, bool, UInt64> tagChangeCallback;

		private List<Action<System.Type,EcsWorld>> worldChangedCallback = new List<Action<System.Type,EcsWorld>>();

		public void AddWorldChangedListener(Action<System.Type,EcsWorld> cb) {
			worldChangedCallback.Add(cb);
		}

		public void RemoveWorldChangedListener(Action<System.Type, EcsWorld> cb) {
			worldChangedCallback.Remove(cb);
		}

		public void FireComponentResizedCallback(Type componentType, EcsWorld world) {
			for (int i = 0, iEnd = worldChangedCallback.Count; i < iEnd; i++) {
				worldChangedCallback[i](componentType, world);
			}
		}

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
		public EcsWorld(int worldIdx, in Config cfg = default) {
			RegisterWorlds(worldIdx, this);
			this._worldIdx = worldIdx+1;
			worldBitmask = _worldIdx << ENTITYID_SHIFT_WORLD;
#else
		public EcsWorld (in Config cfg = default) {
#endif
			// entities.
			var capacity = cfg.Entities > 0 ? cfg.Entities : Config.EntitiesDefault;
			Entities = new EntityData[capacity];
			capacity = cfg.RecycledEntities > 0 ? cfg.RecycledEntities : Config.RecycledEntitiesDefault;
			_recycledEntities = new Queue<int>();
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
			_hashedFilters = new Dictionary<long, EcsFilter>(capacity);
			_allFilters = new List<EcsFilter>(capacity);
			// masks.
			_masks = new Mask[64];
			_masksCount = 0;
#if LEOECSLITE_WORLD_EVENTS
			_eventListeners = new List<IEcsWorldEventListener> (4);
#endif
			_destroyed = false;
		}

		public static int FindFreeWorldSlot() {
			for (int i = 0; i < EcsWorld.MAX_WORLDS; i++) {
				if (EcsWorld.worlds[i] == null) {
					return i;
				}
			}
			return -1;
		}

		public void Destroy() {
			Assert.IsTrue(IsAlive()/*, "Tried to destroy an already destroyed ecsworld"*/);
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (CheckForLeakedEntities()) { throw new Exception($"Empty entity detected before EcsWorld.Destroy()."); }
#endif
			for (var i = _entitiesCount - 1; i >= 0; i--) {
				ref var entityData = ref Entities[i];
				if (entityData.HasComponents) {
					int packedEntity = PackEntity(i);
					DelEntity(packedEntity);
				}
			}
			_destroyed = true;
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
			worlds[WorldArrayIdx] = null;
		}

		/// <summary>
		/// Create a component/tag bitmask by componentIDs and the tag-flag containing all tags
		/// </summary>
		/// <param name="tags"></param>
		/// <param name="componentIDs"></param>
		/// <returns></returns>
		public static Mask.BitMaskData CreateComponentMask(UInt64 tagsSet,UInt64 tagsNotSet) {
			Mask.BitMaskData dataBitmask = new Mask.BitMaskData();
			dataBitmask.tagMaskSet = tagsSet;
			dataBitmask.tagMaskNotSet = tagsNotSet;
			return dataBitmask;
		}

		public static bool IsMaskTrueForEntity(int entity, ref Mask.BitMaskData bitmask) {
			EcsWorld world = GetPackedWorld(entity);
			EntityData data = world.GetEntityData(entity);
			bool result = IsMaskCompatible(ref bitmask, ref data.bitmask, data.bitmask.tagBitMask);
			return result;
		}

		/// <summary>
		/// Checks if the specified entity in a world of this specific world type and if yes return true and output the world in world
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="entity"></param>
		/// <param name="world"></param>
		/// <returns></returns>
		public static bool IsEntityInWorldType<T>(int entity, out T world) {
			EcsWorld entityWorld = GetPackedWorld(entity);
			if (entityWorld is T t) {
				world = t;
				return true;
			}
			world = default;
			return false;
		}

		/// <summary>
		/// Callbacks to be informed if components or entities are created/deleted 
		/// </summary>
		/// <param name="componentChangeCallback"></param>
		public void SetChangeCallbacks(Action<EcsWorld,bool,bool, int> entityChangeCallback, Action<EcsWorld,bool,int,int> componentChangeCallback, Action<EcsWorld, int, bool, UInt64> tagChangeCallback) {
			this.componentChangeCallback = componentChangeCallback;
			this.entityChangeCallback = entityChangeCallback;
			this.tagChangeCallback = tagChangeCallback;
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

#if EZ_USE_ENTITY_HISTORY
		public ulong GetLastEntityTypeAndTag(int packedEntity) {
			ref EntityData entityData = ref GetEntityData(packedEntity);
			return entityData.lastInitialEntityTypeWithTag;
		}
#endif
		public int NewEntity(ulong entityTypeWithTags = 0) {
			Assert.IsTrue(IsAlive()/*, "Tried to add newEntity on destroyed world"*/);

			int entity;
			uint gen = 0;
			if (_recycledEntities.Count >= MIN_RECYCLED_TO_RECYCLE) {
				entity = _recycledEntities.Dequeue();
				ref var entityData = ref Entities[entity];
				entityData.ReactiveDestroyed();
				gen = entityData.Gen;
				// set the entityType as initial bitmask, only having the entityType and no tags attached
				entityData.bitmask.tagBitMask = entityTypeWithTags;
#if EZ_USE_ENTITY_HISTORY
				entityData.lastInitialEntityTypeWithTag = entityTypeWithTags;
#endif
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
				Assert.IsTrue(entity <= MAX_ENTITIES/*, $"Exceeded entityamount:{entity}"*/);
				gen = 0; // we start with generation 0
						 // set the entityType as initial bitmask, only having the entityType and no tags attached
				Entities[entity].ReactiveDestroyed();
				Entities[entity].bitmask.tagBitMask = entityTypeWithTags;
#if EZ_USE_ENTITY_HISTORY
				Entities[entity].lastInitialEntityTypeWithTag = entityTypeWithTags;
#endif

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

			bool changed = OnEntityTagChangeInternal(entity, entityTypeWithTags, 0);
			if (changed && tagChangeCallback != null) {
				// tell the callback the bits that got wiped
				tagChangeCallback(this, entity, true, entityTypeWithTags & MASK_TAG_ENTITY_TYPE_INV);
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
			ulong oldMask = Entities[rawEntity].bitmask.tagBitMask;
			ulong newMask = oldMask & TAGFILTERMASK_ENTITY_TYPE; // only leave the entity-type
			bool changed = OnEntityTagChangeInternal(packedEntity, newMask);
			if (changed && tagChangeCallback != null) {
				// tell the callback the bits that got wiped
				tagChangeCallback(this, packedEntity, false, oldMask & MASK_TAG_ENTITY_TYPE_INV);
			}
		}


		/// <summary>
		/// Add bitmask (or operation) to entity's tagMask multiple 
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="setMask"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void AddMultiTagMask(int packedEntity, UInt64 setMask) {
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, setMask)/*, "EntityType does not match with Tag!"*/);

			int rawEntity = GetPackedRawEntityId(packedEntity);

			ulong newMask = Entities[rawEntity].bitmask.tagBitMask;
			newMask |= setMask;

			bool changed = OnEntityTagChangeInternal(packedEntity, newMask);
			if (changed && tagChangeCallback != null) {
				// tell the callback the bits that got wiped
				tagChangeCallback(this, packedEntity, true, setMask);
			}
		}

		/// <summary>
		/// Check if inputvalue is power of two
		/// </summary>
		/// <param name="x"></param>
		/// <returns></returns>
		public static bool IsPowerOfTwo(ulong x) {
			return (x != 0) && ((x & (x - 1)) == 0);
		}


		public bool IsTagAllowedForEntity(int entity,UInt64 tag) {
			UInt64 tagEntityType = (tag & MASK_TAG_ENTITY_TYPE);
			if (tagEntityType == 0) {
				// globalflag == allowed
				return true;
			}
			UInt64 entityEntityType = GetEntityType(entity);
			return tagEntityType == entityEntityType;
		}

		/// <summary>
		/// Add bitmask (or operation) to entity's tagMask multiple 
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1">tag to be set</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void AddTag(int packedEntity, UInt64 tag1) {
			Assert.IsTrue(IsPacked(packedEntity)/*, $"entity[{packedEntity}] not packed"*/);
			AssertIsEntityValid(packedEntity);

			// TODO: Check if tag is not set at all!?
			Assert.IsTrue(IsPowerOfTwo(tag1 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag1: Tried to set multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag1)/*, "Tag1 not compatible for entity"*/);
			// for now use the multiTag version
			AddMultiTagMask(packedEntity, tag1);
		}


		/// <summary>
		/// Add bitmask (or operation) to entity's tagMask multiple 
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1">tag to be set</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void AddTag(int packedEntity, UInt64 tag1, UInt64 tag2) {
			// TODO: Check if tag is not set at all!?
			Assert.IsTrue(IsPowerOfTwo(tag1 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag1: Tried to set multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsPowerOfTwo(tag2 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag2: Tried to set multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag1)/*, "Tag1 not compatible for entity"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag2)/*, "Tag2 not compatible for entity"*/);


			// for now use the multiTag version
			AddTag(packedEntity, tag1);
			AddTag(packedEntity, tag2);
			//AddMultiTagMask(packedEntity, tag1 | tag2);
		}


		/// <summary>
		/// Add tag to entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1">tag to be set</param>
		/// <param name="tag2">tag to be set</param>
		/// <param name="tag3">tag to be set</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void AddTag(int packedEntity, UInt64 tag1, UInt64 tag2, UInt64 tag3) {
			// TODO: Check if tag is not set at all!?
			Assert.IsTrue(IsPowerOfTwo(tag1 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag1: Tried to set multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsPowerOfTwo(tag2 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag2: Tried to set multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsPowerOfTwo(tag3 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag3: Tried to set multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag1)/*, "Tag1 not compatible for entity"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag2)/*, "Tag2 not compatible for entity"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag3)/*, "Tag3 not compatible for entity"*/);

			// for now use the multiTag version
			//AddMultiTagMask(packedEntity, tag1 | tag2 | tag3);
			AddTag(packedEntity, tag1);
			AddTag(packedEntity, tag2);
			AddTag(packedEntity, tag3);
		}


		/// <summary>
		/// Get tagmask of specified entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public UInt64 GetTagMask(int packedEntity) {
			int rawEntity = GetPackedRawEntityId(packedEntity);
			return Entities[rawEntity].bitmask.tagBitMask;
		}


		/// <summary>
		/// Check if entity has the specific tag-bitmask set (all bits needs to be set)
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="bitmask"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasTagAll(int packedEntity, UInt64 bitmask) {
			int rawEntity = GetPackedRawEntityId(packedEntity);
			ulong entityTagMask = Entities[rawEntity].bitmask.tagBitMask;

			ulong checkTags = bitmask & MASK_TAG_ENTITY_TYPE_INV;
			ulong checkEntityType = bitmask & MASK_TAG_ENTITY_TYPE;

			return (checkTags & entityTagMask) == checkTags && (checkEntityType == 0 || checkEntityType == (entityTagMask & MASK_TAG_ENTITY_TYPE));
		}

		/// <summary>
		/// Check if entity at least one bit of the specific tag-bitmask set
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="bitmask"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasTagSome(int packedEntity, UInt64 bitmask) {
			int rawEntity = GetPackedRawEntityId(packedEntity);
			ulong entityTagMask = Entities[rawEntity].bitmask.tagBitMask;
			ulong checkTags = bitmask & MASK_TAG_ENTITY_TYPE_INV;
			ulong checkEntityType = bitmask & MASK_TAG_ENTITY_TYPE;

			return (checkTags & entityTagMask) > 0 && (checkEntityType == 0 || checkEntityType == (entityTagMask & MASK_TAG_ENTITY_TYPE));
		}


		/// <summary>
		/// Set to the specified mask. Only keeping the entityType
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="setMask"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void SetTagMask(int packedEntity, UInt64 setMask) {
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, setMask)/*, "Tag not compatible to EntityType"*/);

			int rawEntity = GetPackedRawEntityId(packedEntity);

			// use a clean-mask with only the entityType set
			UInt64 oldMask = Entities[rawEntity].bitmask.tagBitMask;
			UInt64 newMask = oldMask & ~TAGFILTERMASK_ENTITY_TYPE;
			newMask |= setMask;

			bool changed = OnEntityTagChangeInternal(packedEntity, newMask);
			
			if (changed && tagChangeCallback != null) {
				// not sure there is a more elegant way. but this is already pretty ok:
				UInt64 stay = oldMask & newMask;   // the bits that stay
				UInt64 wipe = oldMask & ~newMask; // the bits the get kicked
				UInt64 newlySet = ~(stay | wipe);// the bits that are neither staying nor kicked are 0 in (stay|wipe). Inverse=>make those one and the other 0
				
				tagChangeCallback(this, packedEntity, false, wipe);     // tell the callback what bits got wiped
				tagChangeCallback(this, packedEntity, true, newlySet); // tell the callback what bits got activated
			}
		}


		/// <summary>
		/// Unset the specified bits from the entity's tagMask
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="unsetMask"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]/// 
		public void _UnsetTagMask(int packedEntity, UInt64 unsetMask) {
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, unsetMask)/*, "Tag not compatible to EntityType"*/);

			int rawEntity = GetPackedRawEntityId(packedEntity);

			// work directly on the _bitmask-value
			ulong newMask = Entities[rawEntity].bitmask.tagBitMask;
			newMask &= ~(unsetMask & ~TAGFILTERMASK_ENTITY_TYPE);
			bool changed = OnEntityTagChangeInternal(packedEntity, newMask);
			
			if (changed && tagChangeCallback  != null) {
				tagChangeCallback(this, packedEntity, false, unsetMask);     // tell the callback what bits got wiped
			}
		}

		/// <summary>
		/// Remove tag from entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void RemoveTag(int packedEntity, UInt64 tag1) {
			// TODO: Check if tag is set at all!?
			Assert.IsTrue(IsPowerOfTwo(tag1 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag1: Tried to remove multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag1)/*, "Tag1 not compatible for entity"*/);

			// TODO: write optimizied code to only check filters that have this tag involved
			// for now using multicheck. 
			_UnsetTagMask(packedEntity, tag1);
		}

		/// <summary>
		/// Remove tag from entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void RemoveTag(int packedEntity, UInt64 tag1, UInt64 tag2) {
			// TODO: Check if tag is set at all!?
			Assert.IsTrue(IsPowerOfTwo(tag1 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag1: Tried to remove multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsPowerOfTwo(tag2 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag2: Tried to remove multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag1)/*, "Tag1 not compatible for entity"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag2)/*, "Tag2 not compatible for entity"*/);

			RemoveTag(packedEntity, tag1);
			RemoveTag(packedEntity, tag2);
			//// TODO: write optimizied code to only check filters that have this tag involved
			//// for now using multicheck. 
			//_UnsetTagMask(packedEntity, tag1 | tag2);
		}


		/// <summary>
		/// Remove tag from entity
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <param name="tag1"></param>
		/// <param name="tag2"></param>
		/// <param name="tag3"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void RemoveTag(int packedEntity, UInt64 tag1, UInt64 tag2, UInt64 tag3) {
			// TODO: Check if tag is set at all!?
			Assert.IsTrue(IsPowerOfTwo(tag1 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag1: Tried to remove multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsPowerOfTwo(tag2 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag2: Tried to remove multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsPowerOfTwo(tag3 & MASK_TAG_ENTITY_TYPE_INV)/*, "Tag3: Tried to remove multiple tags at once! Don't do this via AddTag(..)"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag1)/*, "Tag1 not compatible for entity"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag2)/*, "Tag2 not compatible for entity"*/);
			Assert.IsTrue(IsTagAllowedForEntity(packedEntity, tag3)/*, "Tag3 not compatible for entity"*/);

			RemoveTag(packedEntity, tag1);
			RemoveTag(packedEntity, tag2);
			RemoveTag(packedEntity, tag3);
			// TODO: write optimizied code to only check filters that have this tag involved
			// for now using multicheck. 
			//_UnsetTagMask(packedEntity, tag1 | tag2 | tag3);
		}


#if ECS_INT_PACKED
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int PackEntity(int plainEntity) {
			int gen = (int)GetEntityGen(plainEntity) << ENTITYID_SHIFT_GEN;
			Assert.IsTrue(plainEntity < MAX_ENTITIES/*, $"Entities out of bounds! {plainEntity} < {MAX_ENTITIES}"*/);
			int packedEntity = worldBitmask | gen | plainEntity;
			return packedEntity;
		}

		/// <summary>
		/// Check if an entity is packed!
		/// </summary>
		/// <param name="entity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool IsPacked(int entity) {
			if (entity == 0) {
				return false;
			}
			// that is not really a good check
			int worldIdx = GetPackedWorldID(entity);
			return worldIdx != 0;
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
			Assert.IsTrue(IsPacked(packedEntity)/*, "UnpackEntity needs packed entities"*/);

			// due to better inlining, dont using the specialized methods here. 
			int rawEntity = packedEntity & ENTITYID_MASK_ENTITY;
			int worldID = ((packedEntity & ENTITYID_MASK_WORLD) >> ENTITYID_SHIFT_WORLD) - 1;
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
			Assert.IsTrue(IsPacked(packedEntity)/*, "UnpackEntityWithWorld needs packed entities"*/);

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


		/// <summary>
		/// Get the generation of this entity packed into the entityID
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static uint GetPackedGen(int packedEntity) {
			Assert.IsTrue(IsPacked(packedEntity)/*, "GetPackedGen needs packed entities"*/);

			uint gen = (uint)(packedEntity & ENTITYID_MASK_GEN) >> ENTITYID_SHIFT_GEN;
			return gen;
		}

		/// <summary>
		/// Get world-id of the world that is packed in the entityId
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int GetPackedWorldID(int packedEntity) {
			int worldId = (packedEntity & ENTITYID_MASK_WORLD) >> ENTITYID_SHIFT_WORLD;
			return worldId;
		}

		/// <summary>
		/// Get entity-type of the specified packed entity
		/// </summary>
		/// <param name="entity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public UInt64 GetEntityType(int entity) {
			ulong tagMask = GetTagMask(entity);
			return tagMask & MASK_TAG_ENTITY_TYPE;
		}

		/// <summary>
		/// Get packed EcsWorld as specicalized EcsWorld-Type
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static T GetPackedWorld<T>(int packedEntity) where T : EcsWorld {
			Assert.IsTrue(IsPacked(packedEntity)/*, "GetPackedWorld needs packed entities"*/);
			int worldId = ((packedEntity & ENTITYID_MASK_WORLD) >> ENTITYID_SHIFT_WORLD) - 1;
			ref EcsWorld _world = ref worlds[worldId];
#if EZ_SANITY_CHECK
			// test the cast
			T cast = (T)_world;
#endif

			T world = UnsafeUtility.As<EcsWorld, T>(ref _world);
			Assert.IsNotNull(world/*, $"world with idx:{worldId} is null!"*/);
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
		/// Check if the world of an entity is still alive (without throwing any asserts)
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		public static bool IsEntityWorldAlive(int packedEntity) {
			Assert.IsTrue(IsPacked(packedEntity)/*, $"Entity[{packedEntity}] not packed"*/);
			int worldId = GetPackedWorldID(packedEntity);
			ref EcsWorld world = ref worlds[worldId - 1];
			bool isAlive = world != null && world.IsAlive();
			return isAlive;
		}

		/// <summary>
		/// Get packed world as base-class EcsWorld
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ref EcsWorld GetPackedWorld(int packedEntity, bool enforceValidEntities=true) {
			Assert.AreNotEqual(0, packedEntity, "0 entity not allowed for GetPackedWorld");
			Assert.IsTrue(IsPacked(packedEntity)/*, $"need packed entity got [{packedEntity}]"*/);
			int worldId = GetPackedWorldID(packedEntity);
			int idx = worldId - 1;
			Assert.IsTrue(idx < worlds.Length/*, $"worldIdx[{idx}] must be < worlds.length[{worlds.Length}]. PackedEntity[{packedEntity}]"*/);
			ref EcsWorld world = ref worlds[idx];
			Assert.IsTrue(world!=null && world.IsAlive()/*, "World not alive anymore!"*/);

#if EZ_SANITY_CHECK 
			if (enforceValidEntities) {
				// check if generation of the packed entity fit with the generation of this entity in the ecs.
				// If there is a mismatch this means that we stored a destroyed entity somewhere!
				uint ecsGen = world.GetEntityGen(packedEntity);
				uint packedGen = EcsWorld.GetPackedGen(packedEntity);
				if (ecsGen != packedGen) {
					throw new Exception($"PackedEntity[{packedEntity}]: There is a generation mismatch![packed:{packedGen} current:{ecsGen}] Seems we stored a destroyed Entity somewhere!");
				}
			}
#endif
			return ref world;
		}

		/// <summary>
		/// Check if entity is valid (generation check and check if entity is alive)
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		public bool IsEntityValid(int packedEntity) {
			bool result = IsAlive() 
							&& ((packedEntity & ENTITYID_MASK_WORLD) == worldBitmask) // entity from this world?
					// && (GetEntityGen(packedEntity) == GetEntityGen(packedEntity)) // same generation as it should have | DONE in IsEntityAlive
					&& IsEntityAliveInternal(packedEntity);
			return result;
		}  

		[System.Diagnostics.Conditional("DEBUG")]
		public void AssertIsEntityValid(int packedEntity) {
			Assert.IsTrue(IsAlive()/*, "ECSWorld already destroyed"*/);
			Assert.AreEqual( (packedEntity & ENTITYID_MASK_WORLD), worldBitmask/*, "Entity-World mismatch!"*/);
			Assert.IsTrue(IsEntityAliveInternal(packedEntity)/*, $"Entity[{packedEntity}] not alive!"*/);
			Assert.AreEqual(GetEntityGen(packedEntity), EcsWorld.GetPackedGen(packedEntity) /*, $"Entity[{packedEntity}]: Generation mismatch!"*/);
		}


		/// <summary>
		/// Get the rawEntityId from packed entity (without the world and gen added bitwise in the entityID)
		/// </summary>
		/// <param name="packedEntity"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int GetPackedRawEntityId(int packedEntity) {
			var rawEntity = packedEntity & ENTITYID_MASK_ENTITY;
			return rawEntity;
		}
#endif

		public void DelEntity(int packedEntity) {
#if ECS_INT_PACKED
			int entity = EcsWorld.GetPackedRawEntityId(packedEntity);
#endif
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (entity < 0 || entity >= _entitiesCount) { throw new Exception("Cant touch destroyed entity."); }
#endif
			ref EntityData entityData = ref Entities[entity];

#if EZ_TRYCATCHMODE
			if (entityData.Destroyed) {
				string warning = $"ERROR: Tried to destroy already destroyed entity:{packedEntity} ! QuickFix:Exit!";
#if EZ_USE_ENTITY_HISTORY
				warning += $"\n       Entity[{packedEntity}] was before {GetLastEntityTypeAndTag(packedEntity)&MASK_TAG_ENTITY_TYPE} with  Tags:{GetLastEntityTypeAndTag(packedEntity) & MASK_TAG_ENTITY_TYPE_INV}";
#endif
				UnityEngine.Debug.LogWarning(warning);
				return;
			}
#elif EZ_SANITY_CHECK
			if (entityData.Destroyed) {
				throw new Exception($"Tried to destroy already destroyed entity: {packedEntity}");
			}
#endif
			// kill components.
			if (entityData.HasComponents) {
				if (entityChangeCallback != null) {
					entityChangeCallback(this,true,false, packedEntity); // tell callback this entity is going to be destroyed 
				}
				var idx = 0;
				while (entityData.HasComponents && idx < _poolsCount) {
					for (; idx < _poolsCount; idx++) {
						if (_pools[idx].Has(entity)) {
							_pools[idx++].Del(packedEntity);
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

			ClearEntityStateMask(packedEntity);
			//entityData.Gen = (uint)(entityData.Gen == short.MaxValue ? -1 : -(entityData.Gen + 1)); // no need to check for end of short. This is done on Gen-Property 
			// entityData.Gen = (uint)(-(entityData.Gen + 1));
			entityData.Destroy();

			_recycledEntities.Enqueue(entity);
#if DEBUG && LEOECSLITE_WORLD_EVENTS
			for (int ii = 0, iMax = _eventListeners.Count; ii < iMax; ii++) {
				_eventListeners[ii].OnEntityDestroyed (entity);
			}
#endif
			if (entityChangeCallback != null) {
				entityChangeCallback(this,false, false, packedEntity); // tell callback this entity is destroyed for good
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

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasPool<T>() where T:struct {
			var poolType = typeof(T);
			return _poolHashes.ContainsKey(poolType);
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
			Assert.IsTrue(typeId >= 0 && typeId < _poolsCount);	
			return _pools[typeId];
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public IEcsPool GetPoolByType(Type type) {
			return _poolHashes.TryGetValue(type, out var pool) ? pool : null;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasPoolWithType(Type type) {
			return _poolHashes.ContainsKey(type);
		}

		public int GetAllEntities(ref int[] entities) {
			var count = _entitiesCount - _recycledEntities.Count;
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
			Mask mask;
			if (_masksCount > 0) {
				mask =  _masks[--_masksCount];
				mask.bitmaskData.Clear();
			} else {
				mask = new Mask(this);
			}
			return mask.Inc<T>();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Mask Filter(ulong tagsSet) {
			Mask mask;
			if (_masksCount > 0) {
				mask = _masks[--_masksCount];
				mask.bitmaskData.Clear();
			} else {
				mask = new Mask(this);
			}
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

		/// <summary>
		/// NEVER use this in game code! This is SUPER SLOW! ONLY FOR DEBUGGING.
		/// </summary>
		/// <param name="entity"></param>
		/// <returns></returns>
		public object[] __GetComponents(int entity) {
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

		public static bool IsPackedEntityAlive(int packedEntity) {
			EcsWorld world = GetPackedWorld(packedEntity,false);
			return world.IsEntityAliveInternal(packedEntity);
		}

		/// <summary>
		/// Needs to be called with unpacked entity 
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsEntityAliveInternal(int packedOrRawEntity, bool outputWarningOnInvalid=false) {
			uint ecsGen = GetEntityGen(packedOrRawEntity);
			if (IsPacked(packedOrRawEntity)) {
				uint packedGen = EcsWorld.GetPackedGen(packedOrRawEntity);
				if (ecsGen != packedGen) {
#if EZ_SANITY_CHECK
					string warning = $"Entity[packedOrRawEntity:{packedOrRawEntity}] not alive: gen mismatch: packedGen:{packedGen} currentGen:{ecsGen}";
#if EZ_USE_ENTITY_HISTORY
					warning += $"\n   was before { GetLastEntityTypeAndTag(packedOrRawEntity) & MASK_TAG_ENTITY_TYPE} with Tags:{ GetLastEntityTypeAndTag(packedOrRawEntity) & MASK_TAG_ENTITY_TYPE_INV}";
#endif
					if (outputWarningOnInvalid) {
						UnityEngine.Debug.LogWarning(warning);
					}
#endif
					return false;
				}
			}

			int entity = GetPackedRawEntityId(packedOrRawEntity);
			bool result = entity >= 0 && entity < _entitiesCount && !Entities[entity].Destroyed;
#if EZ_SANITY_CHECK
			if (!result && outputWarningOnInvalid) {
				try {
					string destroyed = "entity > Entities.Length";
					if (entity < Entities.Length) {
						destroyed = Entities[entity].Destroyed ? "destroyed" : "not destroyed";
					}
					UnityEngine.Debug.LogWarning($"Entity[raw:{entity} packedOrRawEntity:{packedOrRawEntity}] not alive: entity[{entity}] >= 0 && entity[{entity} < _entitiesCount[{_entitiesCount}] && !Entities[entity].Destroyed [{destroyed}]");
				}
				catch(Exception e) {
					UnityEngine.Debug.LogWarning($"Error in logging invalid entity[{entity}]!\n{e.Message}\n{e.StackTrace}\n");
				}
			}
#endif
					return result;
		}

		public void ClearFiltersFromChangedFlag() {
			for (int i = 0, iEnd = _allFilters.Count; i < iEnd; i++) {
				_allFilters[i].Reset();
			}
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
			UInt64 tagmaskHash = mask.TagMaskHash;
			if (tagmaskHash != 0) {
				ulong entityType = (mask.bitmaskData.tagMaskSet & MASK_TAG_ENTITY_TYPE);
				if (entityType > 0) {
					// set the inverse entityType as notsetTag to prevent false positives (like EntityType 1 and 3 and 7)
					mask.bitmaskData.tagMaskNotSet |= (~entityType) & MASK_TAG_ENTITY_TYPE;
				}
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
				//if (entityData.HasComponents && IsMaskCompatible(ref mask.bitmaskData, ref entityData.bitmask, entityData.bitmask.tagBitMask)) {
				if (!entityData.Destroyed && IsMaskCompatible(ref mask.bitmaskData, ref entityData.bitmask, entityData.bitmask.tagBitMask)) {
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
		private bool OnEntityTagChangeInternal(int packedEntity, UInt64 newMask, UInt64 oldBitMask = UInt64.MaxValue) {
			int rawEntity = EcsWorld.GetPackedRawEntityId(packedEntity);
			ref EntityData entityData = ref Entities[rawEntity];
			oldBitMask = oldBitMask == UInt64.MaxValue ? entityData.bitmask.tagBitMask : oldBitMask;
			if (oldBitMask == newMask) {
				// nothing to change
				return false;
			}

			removeFromFilter.Clear();
			addToFilter.Clear();

			// check filters from which we potentially need to remove this entity
			for (int i = 0; i < taggedFilterAmount; i++) {
				ref TaggedFilter filterData = ref _filtersByTagMask[i];
				bool maskCompatible = IsMaskCompatible(ref filterData.filterBitMaskData, ref entityData.bitmask, oldBitMask);
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
					bool newMaskCompatible = IsMaskCompatible(ref filterData.filterBitMaskData, ref entityData.bitmask, newMask);
					if (newMaskCompatible) {
						addToFilter.Add(filterData.filter);
					}
				}
			}

			// now execute removals
			for (int i = 0, iEnd = removeFromFilter.Count; i < iEnd; i++) {
				removeFromFilter[i].RemoveEntity(rawEntity);
			}
			// change entity to new tagMask
			entityData.bitmask.tagBitMask = newMask;
			// now add
			for (int i = 0, iEnd = addToFilter.Count; i < iEnd; i++) {
				addToFilter[i].AddEntity(rawEntity);
			}
			return true;
		}

		/// <summary>
		/// CAUTIONS: Needs to be called with unpacked entity !
		/// </summary>
		public void OnEntityChangeInternal(int entity, int packedEntity, int componentType, ref EntityData.EntityDataBitmask oldBitmask, bool added) {
			var includeList = _filtersByIncludedComponents[componentType];
			var excludeList = _filtersByExcludedComponents[componentType];

			if (added) {
				ref var entityData = ref Entities[entity];
					// add component.
				if (includeList != null) {
					foreach (var filter in includeList) {
						if (IsMaskCompatible(ref filter.GetMask().bitmaskData, ref entityData.bitmask, entityData.bitmask.tagBitMask)) {
							if (filter.SparseEntities[entity] > 0) {
								continue;
								//								throw new Exception("Entity already in filter."); 
							}

							filter.AddEntity(entity);
						}
					}
				}
				if (excludeList != null) {
					foreach (var filter in excludeList) { 
						// check if the entity was in the specific before
						bool isInFilter = filter.SparseEntities[entity] != 0;
						//bool wasInFilter = IsMaskCompatible(ref filter.GetMask().bitmaskData, ref oldBitmask, oldBitmask.tagBitMask);
						// then check if the filter is not compatible anymore => remove // TODO: optimization?
						if (isInFilter && !IsMaskCompatible(ref filter.GetMask().bitmaskData, ref entityData.bitmask, entityData.bitmask.tagBitMask)) {
							if (filter.SparseEntities[entity] == 0) {
								continue;
								//throw new Exception("Entity not in filter."); 
							}
							filter.RemoveEntity(entity);
						}
					}
				}
			} else {
				ref var entityData = ref Entities[entity];

				// remove component.
				if (includeList != null) {
					foreach (var filter in includeList) {
						bool isInFilter = filter.SparseEntities[entity]!=0;
						//bool wasInFilter = IsMaskCompatible(ref filter.GetMask().bitmaskData, ref oldBitmask, oldBitmask.tagBitMask);
						if (isInFilter && !IsMaskCompatible(ref filter.GetMask().bitmaskData, ref entityData.bitmask, Entities[entity].bitmask.tagBitMask)) {
							if (filter.SparseEntities[entity] == 0) {
								continue;
							}
							filter.RemoveEntity(entity);
						}
					}
				}
				if (excludeList != null) {
					foreach (var filter in excludeList) {
						if (IsMaskCompatible(ref filter.GetMask().bitmaskData, ref entityData.bitmask, Entities[entity].bitmask.tagBitMask)) {
							if (filter.SparseEntities[entity] > 0) {
								continue;
							}
							filter.AddEntity(entity);
						}
					}
				}
			}
			if (componentChangeCallback != null) {
				componentChangeCallback(this,added, packedEntity, componentType);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static bool IsTagsMaskCompatible(ref Mask.BitMaskData filterBitmaskData, ulong entityTagMask) {
			bool tagSetApplies;
			bool tagUnsetApplies;
			tagSetApplies = filterBitmaskData.tagMaskSet == 0 || (entityTagMask & filterBitmaskData.tagMaskSet) == filterBitmaskData.tagMaskSet;
			//			tagUnsetApplies = filterBitmaskData.tagMaskNotSet == 0 || (~entitTagMask & filterBitmaskData.tagMaskNotSet) != 0;

			// if not NOT-Mask is set at all we are fine
			// otherwise: &-check the notsetmask (without entityType) with the currentTagMask. if there is any result > 0 there was at least one notsetmask-bit set => fail
			//tagUnsetApplies = filterBitmaskData.tagMaskNotSet == 0 || ((entityTagMask & EcsWorld.MASK_TAG_ENTITY_TYPE_INV) & (filterBitmaskData.tagMaskNotSet)) == 0;
			tagUnsetApplies = filterBitmaskData.tagMaskNotSet == 0 || ((entityTagMask) & (filterBitmaskData.tagMaskNotSet)) == 0;
			bool tagSomeSetApplies = filterBitmaskData.tagMaskSomeSet == 0 || ((entityTagMask) & (filterBitmaskData.tagMaskSomeSet)) > 0;
			return tagSetApplies && tagUnsetApplies && tagSomeSetApplies;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		bool IsMaskCompatibleWithoutComponent(ref Mask.BitMaskData filterBitmaskData, int entity, uint entityTagBitMask, int withoutCom) {
			return false;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool IsMaskCompatible(ref Mask.BitMaskData filterBitmaskData, ref EntityData.EntityDataBitmask entityBitmask, ulong entityTagBitMask) {
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

				includeComponentsApplies = (entityBitmask.componentsBitMask[0] & filterBitmaskData.componentMasks[0]) == filterBitmaskData.componentMasks[0]
								&& (entityBitmask.componentsBitMask[1] & filterBitmaskData.componentMasks[1]) == filterBitmaskData.componentMasks[1];

				excludeComponentsApplies = (entityBitmask.componentsBitMask[0] & filterBitmaskData.componentMasks[2]) == 0
								&& (entityBitmask.componentsBitMask[1] & filterBitmaskData.componentMasks[3]) == 0;

				//TODO: once we are sure this is working do merge all checks to one so that it stops checking on first fail!
				return includeComponentsApplies && excludeComponentsApplies && tagsCompatible;
			}
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
				internal UInt64 tagMaskSet;
				internal UInt64 tagMaskNotSet;
				internal UInt64 tagMaskSomeSet;

				public UInt64 TagMaskSet => tagMaskSet;
				public UInt64 TagMaskNotSet => tagMaskNotSet;
				public UInt64 TagMaskSomeSet => tagMaskSomeSet;

				public UInt64[] componentMasks; // TODO make it fixed

				public void Clear() {
					tagMaskSet = 0;
					tagMaskNotSet = 0;
					tagMaskSomeSet = 0;
					for (int i=0,iEnd=ENTITYDATA_AMOUNT_COMPONENT_BITMASKS * 2; i < iEnd; i++) {
						componentMasks[i] = 0;
					}
				}

				public BitMaskData Clone() {
					BitMaskData data = default;
					data.tagMaskSet = tagMaskSet;
					data.tagMaskNotSet = tagMaskNotSet;
					data.componentMasks = new UInt64[ENTITYDATA_AMOUNT_COMPONENT_BITMASKS * 2];
					for (int i = 0, iEnd = data.componentMasks.Length; i < iEnd; i++) {
						data.componentMasks[i] = componentMasks[i];
					}
					return data;
				}

				///// <summary>
				///// Set this components in the components-include-bitmask
				///// </summary>
				///// <param name="componentIDs"></param>
				///// <returns></returns>
				//public BitMaskData IncComponents(params int[] componentIDs) {
				//	for (int i = 0, iEnd = componentIDs.Length; i < iEnd; i++) {
				//		(int idx, UInt64 mask) = IEcsPool.ComponentID2BitmaskInfo(componentIDs[i]);
				//		componentMasks[idx] |= mask;
				//	}	
				//	return this;
				//}

				///// <summary>
				///// set specified components in the components-exclude-bitmask
				///// </summary>
				///// <param name="componentIDs"></param>
				///// <returns></returns>
				//public BitMaskData ExcComponents(params int[] componentIDs) {
				//	for (int i = 0, iEnd = componentIDs.Length; i < iEnd; i++) {
				//		(int idx, UInt64 mask) = IEcsPool.ComponentID2BitmaskInfo(componentIDs[i]);
				//		componentMasks[idx] &= ~mask; <--- THIS WAS RUBBISH
				//	}
				//	return this;
				//}
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

			public UInt64 TagMaskHash => HashFunction.GetHash(bitmaskData.tagMaskSet,bitmaskData.tagMaskNotSet,bitmaskData.tagMaskSomeSet);

			/// <summary>
			/// Tag-bits that needs to be set to be a valid filter 
			/// </summary>
			/// <param name="bitmask"></param>
			/// <returns></returns>
			public Mask TagsSet(UInt64 bitmask) {
				Assert.AreEqual(0, bitmaskData.tagMaskSet/*, "TagsSet-Mask can only been set once!"*/);
				bitmaskData.tagMaskSet = bitmask;
				return this;
			}

			/// <summary>
			/// Tag-bits that must not be set to be a valid filter
			/// </summary>
			/// <param name="bitmask"></param>
			/// <returns></returns>
			public Mask TagsNotSet(UInt64 bitmask) {
				Assert.AreEqual(0, bitmaskData.tagMaskNotSet/*, "TagsNotSet-Mask can only been set once!"*/);
				// immediately remove entity-type to make it invisible in the notset-check (so we don't need to mask it out on every check)
				bitmaskData.tagMaskNotSet = bitmask & MASK_TAG_ENTITY_TYPE_INV;
				return this;
			}

			/// <summary>
			/// Tag-bits where at least one must be set for the filter to be true
			/// </summary>
			/// <param name="bitmask"></param>
			/// <returns></returns>
			public Mask TagsSomeSet(UInt64 bitmask) {
				Assert.AreEqual(0, bitmaskData.tagMaskSomeSet/*, "TagsSomeSet-Mask can only been set once!"*/);
				// immediately remove entity-type to make it invisible in the someset-check (so we don't need to mask it out on every check)
				bitmaskData.tagMaskSomeSet = bitmask & MASK_TAG_ENTITY_TYPE_INV;
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
			public BitMaskData GetBitmask() {
				return bitmaskData;
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
				Hash = 1;
				Array.Sort(Include, 0, IncludeCount);
				Array.Sort(Exclude, 0, ExcludeCount);
				// calculate hash.
				Hash = unchecked(Hash * 314159 + IncludeCount);
				Hash = unchecked(Hash * 314159 + ExcludeCount);

				// take tagMasks into account for hash-calculation
				Hash = unchecked(Hash * 314159 + bitmaskData.tagMaskNotSet.GetHashCode());
				Hash = unchecked(Hash * 314159 + bitmaskData.tagMaskSet.GetHashCode());
				Hash = unchecked(Hash * 314159 + bitmaskData.tagMaskSomeSet.GetHashCode());
				Assert.AreEqual(4, bitmaskData.componentMasks.Length/*, "This HashAlgorithm expects componentMasks for Length 4(2 inc, 2 exc). Please modify accordingly"*/);
				Hash = unchecked(Hash * 314159 + bitmaskData.componentMasks[0].GetHashCode());
				Hash = unchecked(Hash * 314159 + bitmaskData.componentMasks[1].GetHashCode());
				Hash = unchecked(Hash * 314159 + bitmaskData.componentMasks[2].GetHashCode());
				Hash = unchecked(Hash * 314159 + bitmaskData.componentMasks[3].GetHashCode());
				Type type = typeof(T);
				Hash = unchecked(Hash * 314159 + type.Name.GetHashCode());

				for (int i = 0, iMax = IncludeCount; i < iMax; i++) {
					Hash = unchecked(Hash * 314159 + Include[i]);
				}
				for (int i = 0, iMax = ExcludeCount; i < iMax; i++) {
					Hash = unchecked(Hash * 314161 + Exclude[i]);
				}
				var (filter, isNew) = _world.GetFilterInternal<T>(this, capacity);
				if (!isNew) { 
					Recycle(); 
				}
				return filter;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Recycle() {
				Reset();
				if (_world._masksCount == _world._masks.Length) {
					Array.Resize(ref _world._masks, _world._masksCount << 1);
				}
				_world._masks[_world._masksCount++] = this;
			}
		}




		public unsafe struct EntityData {

			[StructLayout(LayoutKind.Explicit)]
			public unsafe struct EntityDataBitmask {
				/// <summary>
				/// Bitmask representing somekind of state of that entity. DO NOT SET VALUE DIRECTLY!!  THE EcsWorld needs to know if data changed here!!! As long as you know what you are doing....don't do it!
				/// Use .... not sure what, yet.  
				/// </summary>
				// bit 01-04 entity-type (e.g. settler, plant, ... 0=custom for entities, that are more like an helper entity...  there shouldn't be too much real entitiy-types. I hope 16 will be enough
				// bit 05-09 default-tags (tags that makes sense on any entity-type e.g. active,damaged?....
				// bit 10-64 custom-tags (entity-type specific tags)
				[FieldOffset(0)]
				public UInt64 tagBitMask;


				/// <summary>
				/// Two 64bit longs to check for 128 components set to this entity
				/// </summary>
				[UnityEngine.Tooltip("Two 64bit longs to check for 128 components set to this entity")]

#if !USE_FIXED_ARRAYS
				public UInt64[] componentsBitMask;
#else
				[MarshalAs(UnmanagedType.ByValArray/*, SizeConst = 123*/)]
				[FieldOffset(sizeof(UInt64))]
				[NonSerialized]
				public fixed UInt64 componentsBitMask[EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS];

				// I'm using the following two variables to safely read and write the positions of the upper fixed array
				// it seems that the (de)serializer had problems with the second idx. With this 'data/position-sharing' I could
				// make it work.D
				[FieldOffset(sizeof(UInt64))]
				public UInt64 c1;
				[FieldOffset(2*sizeof(UInt64))]
				public UInt64 c2;
#endif






#if DEBUG

				bool IsNull => componentsBitMask[1] == 0;
#endif
				public ulong GetComponentBitmask(int idx) {
					return componentsBitMask[idx];
				}
			}

			public const uint MASK_GEN = (0b1111111) << 0;
			public const uint MASK_HAS_COMPONENTS = (1 << 7);
			public const uint MASK_DESTROYED = (1 << 8);

			// bit 00-06 gen
			// bit 07    has components
			// bit 08    destroyed
			// bit 09-31 [free to use]
			public UInt32 entityInfo;

			public unsafe EntityDataBitmask bitmask;

#if EZ_USE_ENTITY_HISTORY
			/// <summary>
			/// keep what entitytype this entity was last time it was created via NewEntity(...)
			/// </summary>
			public ulong lastInitialEntityTypeWithTag;
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

				//if (newGen == 0) {
				//	int a = 0;
				//}
			}

			public void ReactiveDestroyed() {
				entityInfo &= ~MASK_DESTROYED;
				bitmask.componentsBitMask[0] = 0;
				bitmask.componentsBitMask[1] = 0;
				bitmask.tagBitMask = 0;
				// no need to set a new generation as this is done by the destruction
			}


			public uint Gen => (entityInfo & MASK_GEN);

			public bool HasComponents => (entityInfo & MASK_HAS_COMPONENTS) > 0;

			public bool Destroyed => (entityInfo & MASK_DESTROYED) > 0;

			///// <summary>
			///// TagMaskFilterKey used to lookup corresponding EcsFilters that fit to this tagBitMask
			///// </summary>
			//[UnityEngine.Tooltip("TagMaskFilterKey used to lookup corresponding EcsFilters that fit to this tagBitMask")]
			//public UInt32 TagMaskFilterKey => (bitmask.tagBitMask << 32) + (~bitmask.tagBitMask);

			/// <summary>
			/// Get entity-type (stored in the tagBitMask)
			/// </summary>
			[UnityEngine.Tooltip("Get entity-type (stored in the tagBitMask)")]
			public ulong EntityType => bitmask.tagBitMask & TAGFILTERMASK_ENTITY_TYPE;

			public void _UpdateHasComponents() {
#if !USE_FIXED_ARRAYS
				if (bitmask.componentsBitMask == null) {
					bitmask.componentsBitMask = new UInt64[EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS];
				}
#endif
				for (int i = 0; i < EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS; i++) {
					if (bitmask.componentsBitMask[i] > 0) {
						entityInfo |= MASK_HAS_COMPONENTS;
						return;
					}
				}
				entityInfo &= ~MASK_HAS_COMPONENTS;
			}


			/// <summary>
			/// Set specific bitmask (| bitwise) returns the old mask
			/// </summary>
			/// <param name="componentBitmaskId"></param>
			/// <param name="setMask"></param>
			/// <returns></returns>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public EntityDataBitmask SetComponentBit(int componentBitmaskId, UInt64 setMask) {
#if !USE_FIXED_ARRAYS
				if (bitmask.componentsBitMask == null) {
					bitmask.componentsBitMask = new UInt64[EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS];
				}
#endif
				bitmask.componentsBitMask[componentBitmaskId] |= setMask;
				_UpdateHasComponents();
				return bitmask;
			}

			
			/// <summary>
			/// Unset specific bitmask. Returns the old mask
			/// </summary>
			/// <param name="componentBitmaskId"></param>
			/// <param name="unsetMask"></param>
			/// <returns></returns>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public EntityDataBitmask UnsetComponentBit(int componentBitmaskId, UInt64 unsetMask) {
				EntityDataBitmask oldMask = bitmask;
#if !USE_FIXED_ARRAYS
				if (bitmask.componentsBitMask == null) {
					bitmask.componentsBitMask = new UInt64[EcsWorld.ENTITYDATA_AMOUNT_COMPONENT_BITMASKS];
				}
#endif
				bitmask.componentsBitMask[componentBitmaskId] &= ~unsetMask;
				_UpdateHasComponents();
				return bitmask;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public bool CheckSingleComponent(int componentBitmaskIdx, UInt64 componentBitMask) {
				return ( bitmask.componentsBitMask[componentBitmaskIdx] & componentBitMask) == componentBitMask;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public bool HasTagMask(ulong tagmask) {
				return (bitmask.tagBitMask & tagmask) == tagmask;
			}


			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public bool IsTagsMaskCompatible(ref Mask.BitMaskData filterBitmaskData, uint entityTagMask) {
				bool tagSetApplies = filterBitmaskData.tagMaskSet == 0 || (entityTagMask & filterBitmaskData.tagMaskSet) == filterBitmaskData.tagMaskSet;
				bool tagUnsetApplies = filterBitmaskData.tagMaskNotSet == 0 || (~entityTagMask & filterBitmaskData.tagMaskNotSet) != 0;
				bool tagSomeApplies = filterBitmaskData.tagMaskSomeSet == 0 || (entityTagMask & filterBitmaskData.tagMaskSomeSet) > 0;
				return tagSetApplies && tagUnsetApplies && tagSomeApplies;
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