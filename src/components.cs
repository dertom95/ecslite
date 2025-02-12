﻿// ----------------------------------------------------------------------------
// The MIT License
// Lightweight ECS framework https://github.com/Leopotam/ecslite
// Copyright (c) 2021-2022 Leopotam <leopotam@gmail.com>
// ----------------------------------------------------------------------------

using System;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
using static Leopotam.EcsLite.EcsWorld;
using static Leopotam.EcsLite.EcsWorld.EntityData;

#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace Leopotam.EcsLite {
	public interface IEcsPool {
		/// <summary>
		/// Returns bitmaskFieldID and bitmask for the component this pool is managing
		/// </summary>
		public (int,ulong) BitmaskInfo { get; }
		int SparseArraySize();
		void Resize(int capacity);
		bool Has(int entity);
		void Del(int entity);
		void AddRaw(int entity, object dataRaw);
		object GetRaw(int entity);
		void SetRaw(int entity, object dataRaw);
		int GetId();
		Type GetComponentType();

		/// <summary>
		/// Calculate position in component-bitmask (ulong-array-idx and bitmask to apply) by the componentId
		/// </summary>
		/// <param name="componentId"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		protected static (int, UInt64) ComponentID2BitmaskInfo(int componentId) {
			int _bitmaskFieldId = componentId / 64;
			UInt64 _componentBitmask = (UInt64)1 << (componentId % 64);
			return (_bitmaskFieldId, _componentBitmask);
		}


	}

	public interface IEcsAutoReset<T> where T : struct {
		void AutoReset(ref T c);
	}


#if ENABLE_IL2CPP
	[Il2CppSetOption (Option.NullChecks, false)]
	[Il2CppSetOption (Option.ArrayBoundsChecks, false)]
#endif
	public sealed class EcsPool<T> : IEcsPool where T : struct {
		readonly Type _type;
		readonly EcsWorld _world;
		readonly int _id;
		/// <summary>
		/// The idx inside of EntityData's componentBitmask-array
		/// </summary>
		public int _bitmaskFieldId;
		/// <summary>
		/// The component mask to add to the corresponding componentBitmask
		/// </summary>
		public UInt64 _componentBitmask;

		public (int, ulong) BitmaskInfo => (_bitmaskFieldId,_componentBitmask);

		readonly AutoResetHandler _autoReset;
		// 1-based index.
		public T[] _denseItems;
		public int[] _sparseItems;
		public int _denseItemsCount;
		public int[] _recycledItems;
		public int _recycledItemsCount;

		public int SparseArraySize() => _sparseItems.Length;
#if ENABLE_IL2CPP && !UNITY_EDITOR
		T _autoresetFakeInstance;
#endif

		internal EcsPool(EcsWorld world, int id, int denseCapacity, int sparseCapacity, int recycledCapacity) {
			if (id > 128) {
				throw new Exception($"Exceeded real component amount! ComponentID {id} is not supported! Modify EcsWorld.EntityData for more capacity");
			}
			_type = typeof(T);
			_world = world;
			_id = id;
			// calculate bitwise representation of this component for its world
			(_bitmaskFieldId, _componentBitmask) = IEcsPool.ComponentID2BitmaskInfo(id);

			_denseItems = new T[denseCapacity + 1];
			_sparseItems = new int[sparseCapacity];
			_denseItemsCount = 1;
			_recycledItems = new int[recycledCapacity];
			_recycledItemsCount = 0;
			var isAutoReset = typeof(IEcsAutoReset<T>).IsAssignableFrom(_type);
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (!isAutoReset && _type.GetInterface("IEcsAutoReset`1") != null) {
				throw new Exception($"IEcsAutoReset should have <{typeof(T).Name}> constraint for component \"{typeof(T).Name}\".");
			}
#endif
			if (isAutoReset) {
				var autoResetMethod = typeof(T).GetMethod(nameof(IEcsAutoReset<T>.AutoReset));
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
				if (autoResetMethod == null) {
					throw new Exception(
						$"IEcsAutoReset<{typeof(T).Name}> explicit implementation not supported, use implicit instead.");
				}
#endif
				_autoReset = (AutoResetHandler)Delegate.CreateDelegate(
					typeof(AutoResetHandler),
#if ENABLE_IL2CPP && !UNITY_EDITOR
					_autoresetFakeInstance,
#else
					null,
#endif
					autoResetMethod);
			}
		}

		public void OverrideInternalBitmask(int bitmaskIdx, ulong bitmask) {
			this._bitmaskFieldId = bitmaskIdx;
			this._componentBitmask = bitmask;
		}


#if UNITY_2020_3_OR_NEWER
		[UnityEngine.Scripting.Preserve]
#endif
		void ReflectionSupportHack() {
			_world.GetPool<T>();
			_world.Filter<T>().Exc<T>().End();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public EcsWorld GetWorld() {
			return _world;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int GetId() {
			return _id;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Type GetComponentType() {
			return _type;
		}

		void IEcsPool.Resize(int capacity) {
			Array.Resize(ref _sparseItems, capacity);
		}

		object IEcsPool.GetRaw(int entity) {
			return Get(entity);
		}

		void IEcsPool.SetRaw(int entity, object dataRaw) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (dataRaw == null || dataRaw.GetType() != _type) { throw new Exception("Invalid component data, valid \"{typeof (T).Name}\" instance required."); }
			if (_sparseItems[entity] <= 0) { throw new Exception($"Component \"{typeof(T).Name}\" not attached to entity."); }
#endif
			_denseItems[_sparseItems[entity]] = (T)dataRaw;
			if (typeof(IEntity).IsAssignableFrom(typeof(T))) {
				unsafe {
					ref EntityHeader ent = ref UnsafeUtility.As<object, EntityHeader>(ref dataRaw);
					ent.entity = entity;
				}
			}
		}

		void IEcsPool.AddRaw(int entity, object dataRaw) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (dataRaw == null || dataRaw.GetType() != _type) { throw new Exception("Invalid component data, valid \"{typeof (T).Name}\" instance required."); }
#endif
			ref var data = ref Add(entity);
			data = (T)dataRaw;
			if (typeof(IEntity).IsAssignableFrom(typeof(T))) {
				unsafe {
					ref EntityHeader ent = ref UnsafeUtility.As<T, EntityHeader>(ref data);
					ent.entity = entity;
				}
			}
		}

		public T[] GetRawDenseItems() {
			return _denseItems;
		}

		public ref int GetRawDenseItemsCount() {
			return ref _denseItemsCount;
		}

		public int[] GetRawSparseItems() {
			return _sparseItems;
		}

		public int[] GetRawRecycledItems() {
			return _recycledItems;
		}

		public ref int GetRawRecycledItemsCount() {
			return ref _recycledItemsCount;
		}

		public ref T Add(int entity) {
#if ECS_INT_PACKED
			int packedEntity = entity;
			// work with the raw-entity
			entity = EcsWorld.GetPackedRawEntityId(packedEntity);
#endif
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (!_world.IsEntityAliveInternal(entity)) { throw new Exception("Cant touch destroyed entity."); }
			if (_sparseItems[entity] > 0) { throw new Exception($"Component \"{typeof(T).Name}\" already attached to entity."); }
#endif
			int idx;
			if (_recycledItemsCount > 0) {
				idx = _recycledItems[--_recycledItemsCount];
			} else {
				idx = _denseItemsCount;
				if (_denseItemsCount == _denseItems.Length) {
					Array.Resize(ref _denseItems, _denseItemsCount << 1);
					// the memory-positions of the components changed with the resize => for now we mark all filters that they should recatch the data, as we can't be sure what PtrRefs are set in the filterdata
					_world._MarkFiltersDirty();
					_world.FireComponentResizedCallback(typeof(T), GetWorld());
				}
				_denseItemsCount++;
				_autoReset?.Invoke(ref _denseItems[idx]);
			}
			_sparseItems[entity] = idx;
			ref EcsWorld.EntityData entityData = ref _world.Entities[entity];

			var oldMask = entityData.SetComponentBit(_bitmaskFieldId, _componentBitmask);

			_world.OnEntityChangeInternal(entity, packedEntity, _id, ref oldMask, true);
#if LEOECSLITE_WORLD_EVENTS
#if ECS_INT_PACKED
			_world.RaiseEntityChangeEvent(packedEntity);
#else
			_world.RaiseEntityChangeEvent(entity);
#endif
#endif

			ref T data = ref _denseItems[idx];

			if (typeof(IEntity).IsAssignableFrom(typeof(T))) {
				unsafe {
					ref EntityHeader ent = ref UnsafeUtility.As<T, EntityHeader>(ref data);
					ent.entity = packedEntity;
				}
			}

			return ref data;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ref T Get(int entity) {
#if ECS_INT_PACKED
			int packedEntity = entity;
			// work with the raw-entity
			entity = EcsWorld.GetPackedRawEntityId(packedEntity);
#endif
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (!_world.IsEntityAliveInternal(entity)) { throw new Exception("Cant touch destroyed entity."); }
			if (_sparseItems[entity] == 0) {
				throw new Exception($"[{_world.PackEntity(entity)}] Cant get \"{typeof(T).Name}\" component - not attached.");
			}
#endif
			return ref _denseItems[_sparseItems[entity]];
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Has(int entity) {
#if ECS_INT_PACKED
			int packedEntity = entity;
			// work with the raw-entity
			entity = EcsWorld.GetPackedRawEntityId(packedEntity);
#endif
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (!_world.IsEntityAliveInternal(entity)) { throw new Exception("Cant touch destroyed entity."); }
#endif
			return _sparseItems[entity] > 0;
		}

		public void Del(int packedEntity) {
#if ECS_INT_PACKED
			// work with the raw-entity
			int entity = EcsWorld.GetPackedRawEntityId(packedEntity);
#endif
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
			if (!_world.IsEntityAliveInternal(entity)) { throw new Exception("Cant touch destroyed entity."); }
#endif
			ref var sparseData = ref _sparseItems[entity];
			if (sparseData > 0) {

				ref var entityData = ref _world.Entities[entity];

				var oldMask = entityData.UnsetComponentBit(_bitmaskFieldId, _componentBitmask);


				_world.OnEntityChangeInternal(entity, packedEntity, _id, ref oldMask, false);
				if (_recycledItemsCount == _recycledItems.Length) {
					Array.Resize(ref _recycledItems, _recycledItemsCount << 1);
				}
				_recycledItems[_recycledItemsCount++] = sparseData;
				if (_autoReset != null) {
					_autoReset.Invoke(ref _denseItems[sparseData]);
				} else {
					_denseItems[sparseData] = default;
				}
				sparseData = 0;
				//entityData.UpdateHasComponents();
#if LEOECSLITE_WORLD_EVENTS
#if ECS_INT_PACKED
				_world.RaiseEntityChangeEvent (packedEntity);
#else
				_world.RaiseEntityChangeEvent (entity);
#endif
#endif
				if (!entityData.HasComponents) {
#if ECS_INT_PACKED
					_world.DelEntity(packedEntity);
#else
					_world.DelEntity(entity);
#endif
				}
			}
		}

		/// <summary>
		/// Iterates over sparse-array to check all entities that have a component of this type attached 
		/// Needs a big enough array (int[entityAmount in world] for the worst case)
		/// </summary>
		/// <returns>Array with entity ids or null if no entity has this component type attached</returns>
		public int __CollectEntities(int[] result) {
			if (_denseItemsCount == 0) {
				return 0;
			}
			int idx = 0;
			int current = 0;
			for (int i = 0, count = _sparseItems.Length; i < count && idx < _denseItemsCount; i++) {
				if ((current = _sparseItems[i]) != 0) {
					result[idx] = i; // the sparse-elements are stored as entityId+1 (so that sparse==0 can be the not set-value!?)
					idx++;
				}
			}
			return idx;
		}

		delegate void AutoResetHandler(ref T component);
	}

	public sealed class ShallowPool<T> : IEcsPool {
		public (int, ulong) BitmaskInfo => (bitmaskIdx,bitmask);

		EcsWorld world;
		int poolId;
		int bitmaskIdx;
		ulong bitmask;
		readonly Type type;
		/// <summary>
		/// Just a empty struct-instance to be returned bei GetRaw(..)
		/// </summary>
		readonly T returnData;

		public ShallowPool(EcsWorld world,int poolId) {
			this.world = world;
			this.poolId = poolId;
			this.type = typeof(T);
			returnData = (T)Activator.CreateInstance(type);
		}

		public void OverrideInternalBitmask(int bitmaskIdx, ulong bitmask) {
			this.bitmask = bitmask;
			this.bitmaskIdx = bitmaskIdx;
		}
		
		public void Add(int packedEntity) {
			int rawEntity = GetPackedRawEntityId(packedEntity);
			ref EntityData entityData = ref world.GetEntityData(rawEntity);
			
			entityData.SetComponentBit(bitmaskIdx, bitmask);

			EntityDataBitmask oldMask = entityData.SetComponentBit(bitmaskIdx, bitmask);
			world.OnEntityChangeInternal(rawEntity, packedEntity, poolId, ref oldMask, true);
		}

		public void AddRaw(int entity, object dataRaw) {
			Add(entity);
		}

		public void Del(int packedEntity) {
			int rawEntity = GetPackedRawEntityId(packedEntity);
			ref EntityData entityData = ref world.GetEntityData(packedEntity);

			EntityDataBitmask oldMask = entityData.UnsetComponentBit(bitmaskIdx, bitmask);

			world.OnEntityChangeInternal(rawEntity, packedEntity, poolId, ref oldMask, false);
		}

		public Type GetComponentType() {
			return type;
		}

		public int GetId() {
			return poolId;
		}

		public object GetRaw(int entity) {
			if (Has(entity)) {
				return returnData;
			} else {
				return null;
			}
		}

		public bool Has(int entity) {
			ref EntityData entityData = ref world.GetEntityData(entity);
			bool found = entityData.CheckSingleComponent(bitmaskIdx, bitmask);
			return found;
		}

		public void Resize(int capacity) {
		}

		public void SetRaw(int entity, object dataRaw) {
			Add(entity);
		}

		public int SparseArraySize() {
			throw new NotImplementedException();
		}
	}

}
