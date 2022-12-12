// ----------------------------------------------------------------------------
// The MIT License
// Lightweight ECS framework https://github.com/Leopotam/ecslite
// Copyright (c) 2021-2022 Leopotam <leopotam@gmail.com>
// ----------------------------------------------------------------------------
//#define LEOECSLITE_FILTER_EVENTS // for some reason unity did not add apply the define (for sure a local problem,remove if not needed anymore)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine.Assertions;

#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace Leopotam.EcsLite {
#if ECS_INT_PACKED
    public interface IFilterData
    {
        int PackedEntity { get; set; } 
        void SetData();
    }

	/// <summary>
	/// Use those filters to define FilterData. Do not use IFilterData directly (at least not it you want the FilterData being generated for you! )
	/// </summary>
	public interface IFilterDataDefinition { }
	/// <summary>
	/// Use those filters to define FilterData. Do not use IFilterData directly (at least not it you want the FilterData being generated for you! )
	/// </summary>
	public interface IFilterData<A> : IFilterDataDefinition { }
	/// <summary>
	/// Use those filters to define FilterData. Do not use IFilterData directly (at least not it you want the FilterData being generated for you! )
	/// </summary>
	public interface IFilterData<A, B> : IFilterDataDefinition { }
	/// <summary>
	/// Use those filters to define FilterData. Do not use IFilterData directly (at least not it you want the FilterData being generated for you! )
	/// </summary>
	public interface IFilterData<A, B, C> : IFilterDataDefinition { }
	/// <summary>
	/// Use those filters to define FilterData. Do not use IFilterData directly (at least not it you want the FilterData being generated for you! )
	/// </summary>
	public interface IFilterData<A, B, C, D> : IFilterDataDefinition { }

	public struct NoFilterData : IFilterData
    {
        public int PackedEntity { get; set; }
        public void SetData() {
        }
    }
#endif

#if LEOECSLITE_FILTER_EVENTS
    public interface IEcsFilterEventListener {
        void OnEntityAdded (int entity);
        void OnEntityRemoved (int entity);
    }
#endif
#if ENABLE_IL2CPP
    [Il2CppSetOption (Option.NullChecks, false)]
    [Il2CppSetOption (Option.ArrayBoundsChecks, false)]
#endif
    public abstract class EcsFilter {
        internal abstract void ResizeSparseIndex(int capacity);
        internal abstract void AddEntity(int entity);
        internal abstract void RemoveEntity(int entity,int removeDenseIdx=-1);
        internal abstract EcsWorld.Mask GetMask();
        internal int[] SparseEntities;
        internal bool updateFilters = false;
    }

    public sealed class EcsFilter<T> : EcsFilter where T:IFilterData {
        readonly EcsWorld _world;
        readonly EcsWorld.Mask _mask;
        int[] _denseEntities;
        T[] _filterData=null;
        int _entitiesCount;
        int _lockCount;
        DelayedOp[] _delayedOps;
        int _delayedOpsCount;

		// TODO: replace this with an array or list. Queue won't work for shared Filters
		private Queue<int> queueNewEntities;
		private Queue<int> queueRemovedEntities;
#if EZ_SANITY_CHECK
		HashSet<int> doubleEntryCheckNew = new HashSet<int>();
		HashSet<int> doubleEntryCheckRemoved = new HashSet<int>();
#endif

#if LEOECSLITE_FILTER_EVENTS
        IEcsFilterEventListener[] _eventListeners = new IEcsFilterEventListener[4];
        int _eventListenersCount;

#endif

#if EZ_SANITY_CHECK
		public string name;

		public void DEBUG_SetName(string filterName) {
			this.name = filterName;
		}
#endif

		internal EcsFilter (EcsWorld world, EcsWorld.Mask mask, int denseCapacity, int sparseCapacity) {
            if (typeof(T) != typeof(NoFilterData)) {
                _filterData = new T[denseCapacity];
            }
            _world = world;
            _mask = mask;
            _denseEntities = new int[denseCapacity];
            SparseEntities = new int[sparseCapacity];
            _entitiesCount = 0;
            _delayedOps = new DelayedOp[512];
            _delayedOpsCount = 0;
            _lockCount = 0;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public EcsWorld GetWorld () {
            return _world;
        }

		/// <summary>
		/// When enabling the inOutQueue! It is important not to forget to dequeue both queues. Otherwise the system will throw asserts
		/// </summary>
		public void EnableInOutQueue() {
			queueNewEntities = new Queue<int>();
			queueRemovedEntities = new Queue<int>();
		}

		public void DisableInOutQueue() {
			queueNewEntities = null;
			queueNewEntities = null;
		}

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public int GetEntitiesCount () {
            return _entitiesCount;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public int[] GetRawEntities () {
            return _denseEntities;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public int[] GetSparseIndex () {
            return SparseEntities;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public Enumerator<T> GetEnumerator () {
            _lockCount++;
            return new Enumerator<T> (this);
        }

		public DelayedOp[] _GetDelayedOps() {
			return _delayedOps;
		}

		public int _GetDelayedOpsCount() {
			return _delayedOpsCount;
		}

#if LEOECSLITE_FILTER_EVENTS
        public void AddEventListener (IEcsFilterEventListener eventListener) {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (eventListener == null) { throw new Exception ("Listener is null."); }
#endif
            if (_eventListeners.Length == _eventListenersCount) {
                Array.Resize (ref _eventListeners, _eventListenersCount << 1);
            }
            _eventListeners[_eventListenersCount++] = eventListener;
        }

        public void RemoveEventListener (IEcsFilterEventListener eventListener) {
            for (var i = 0; i < _eventListenersCount; i++) {
                if (_eventListeners[i] == eventListener) {
                    _eventListenersCount--;
                    // cant fill gap with last element due listeners order is important.
                    Array.Copy (_eventListeners, i + 1, _eventListeners, i, _eventListenersCount - i);
                    break;
                }
            }
        }
#endif

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        override internal void ResizeSparseIndex (int capacity) {
            Array.Resize (ref SparseEntities, capacity);
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        override internal EcsWorld.Mask GetMask () {
            return _mask;
        }

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasEntitiesInNewQueue() {
			Assert.IsNotNull(queueNewEntities);
			return queueNewEntities.Count > 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasEntitiesInRemovedQueue() {
			Assert.IsNotNull(queueRemovedEntities);
			return queueRemovedEntities.Count > 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int DequeueNextNewEntity() {
			Assert.IsNotNull(queueNewEntities);
			Assert.IsTrue(queueNewEntities.Count>0);
			int newEntity = queueNewEntities.Dequeue();
#if EZ_SANITY_CHECK
			doubleEntryCheckNew.Remove(newEntity);
#endif
			return newEntity;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int DequeueNextRemovedEntity() {
			Assert.IsNotNull(queueRemovedEntities);
			Assert.IsTrue(queueRemovedEntities.Count > 0);
			int newEntity = queueRemovedEntities.Dequeue();
#if EZ_SANITY_CHECK
			doubleEntryCheckNew.Remove(newEntity);
#endif
			return newEntity;
		}

		[MethodImpl (MethodImplOptions.AggressiveInlining)]
        override internal void AddEntity (int entity) {
            if (AddDelayedOp (true, entity)) { return; }
            if (_entitiesCount == _denseEntities.Length) {
                Array.Resize (ref _denseEntities, _entitiesCount << 1);
				if (_filterData != null) {
					Array.Resize(ref _filterData, _entitiesCount << 1);
				}
			}
#if ECS_INT_PACKED
            int packedEntity = _world.PackEntity(entity);
            int densePosition = _entitiesCount;
            _denseEntities[densePosition] = packedEntity;
			//UnityEngine.Debug.Log($"{entity} Count:" + _entitiesCount);
			if (_filterData != null) {
                _filterData[densePosition].PackedEntity = packedEntity;
                _filterData[densePosition].SetData();
            }
            _entitiesCount++;

			if (queueNewEntities != null) {
				queueNewEntities.Enqueue(packedEntity);
#if EZ_SANITY_CHECK
				Assert.IsFalse(doubleEntryCheckRemoved.Contains(packedEntity),$"Problem using queueInOut: just added Entity:{packedEntity} to filter! But this is entity is already marked as removed! This problem occurs if the entity first didn't apply to the filter and then do again in one frame!Maybe you need to use another approach!");
				doubleEntryCheckNew.Add(packedEntity);
#endif
			}
#else
            _denseEntities[_entitiesCount++] = entity;
#endif
			SparseEntities[entity] = _entitiesCount;
#if LEOECSLITE_FILTER_EVENTS

            ProcessEventListeners (true, packedEntity);
#endif
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        override internal void RemoveEntity (int entity,int _removeDenseIdx=-1) {
			if (SparseEntities[entity] == 0 && _removeDenseIdx==-1) {
				// entity already removed or marked to be removed
				return;
			}
            if (AddDelayedOp (false, entity, -1 /*SparseEntities[entity]-1*/)) {
				// mark sparseEnttiy as removed to prevent multiple remove-ops 
				// SparseEntities[entity] = 0;
				return; 
			}
			if (queueRemovedEntities != null) {
				int packedEntity = _world.PackEntity(entity);
				queueRemovedEntities.Enqueue(packedEntity);
#if EZ_SANITY_CHECK
				Assert.IsFalse(doubleEntryCheckNew.Contains(packedEntity), $"Problem using queueInOut: just removed Entity:{packedEntity}! But this entity is already marked as new! This might be due to the Entity applied to the filter and stopped do this in one frame. Check the filter or how you add/remove the tags and components! Maybe you might need to use another approach");
				doubleEntryCheckRemoved.Add(packedEntity);
#endif

			}
#if LEOECSLITE_FILTER_EVENTS
#if ECS_INT_PACKED
            ProcessEventListeners(false, packedEntity);
#else
            ProcessEventListeners(false, entity);
#endif
#endif
			var removeDenseIdx = _removeDenseIdx!=-1 ? _removeDenseIdx : (SparseEntities[entity] - 1);
            SparseEntities[entity] = 0;
            _entitiesCount--;
            if (removeDenseIdx < _entitiesCount) {
                _denseEntities[removeDenseIdx] = _denseEntities[_entitiesCount];
                int denseEntityPacked = _denseEntities[removeDenseIdx];
                int sparseIdx = EcsWorld.GetPackedRawEntityId(denseEntityPacked);
                SparseEntities[sparseIdx] = removeDenseIdx + 1;
                if (_filterData != null) {
                    _filterData[removeDenseIdx] =_filterData[_entitiesCount];
                }
            }
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        bool AddDelayedOp (bool added, int entity, int removeDenseIdx=-1) {
            if (_lockCount <= 0) { return false; }
            if (_delayedOpsCount == _delayedOps.Length) {
                Array.Resize (ref _delayedOps, _delayedOpsCount << 1);
            }
            ref var op = ref _delayedOps[_delayedOpsCount++];
            op.Added = added;
            op.Entity = entity;
			op.removedDenseIdx = removeDenseIdx;
            return true;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        void Unlock () {
#if DEBUG && !LEOECSLITE_NO_SANITIZE_CHECKS
            if (_lockCount <= 0) { throw new Exception ($"Invalid lock-unlock balance for \"{GetType ().Name}\"."); }
#endif
            _lockCount--;
            if (_lockCount == 0 && _delayedOpsCount > 0) {
                for (int i = 0, iMax = _delayedOpsCount; i < iMax; i++) {
                    ref var op = ref _delayedOps[i];

					var entityData = _world.GetEntityData(op.Entity);
					if (EcsWorld.IsMaskCompatible(ref _mask.bitmaskData, ref entityData.bitmask, entityData.bitmask.tagBitMask)) {
						// current state of that entity is valid for this filter. Only add entity if not already in SparseEntities
						if (SparseEntities[op.Entity] == 0) {
							AddEntity(op.Entity);
						} 
					} else {
						// current state(mask) is not compatible to filter. Remove if in filter
						if (SparseEntities[op.Entity] != 0) {
							RemoveEntity(op.Entity);
						}
					}

					//if (op.Added) {
     //                   AddEntity (op.Entity);
     //               } else {
     //                   RemoveEntity (op.Entity,op.removedDenseIdx);
     //               }
                }

				_delayedOpsCount = 0;
            }
        }

#if LEOECSLITE_FILTER_EVENTS
        void ProcessEventListeners (bool isAdd, int entity) {
            if (isAdd) {
                for (var i = 0; i < _eventListenersCount; i++) {
                    _eventListeners[i].OnEntityAdded (entity);
                }
            } else {
                for (var i = 0; i < _eventListenersCount; i++) {
                    _eventListeners[i].OnEntityRemoved (entity);
                }
            }
        }
#endif

        public struct Enumerator<S> : IDisposable where S:T{
            readonly EcsFilter<S> _filter;
            readonly S[] _filterData;
            readonly int[] _entities;
            readonly int _count;
            int _idx;
            

            public Enumerator (EcsFilter<S> filter) {
                _filter = filter;
                _entities = filter._denseEntities;
                _count = filter._entitiesCount;
                _filterData = filter._filterData;
                _idx = -1;
            }

            public (int,T) Current {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get {
                    if (_filterData != null) {
                        if (_filter.updateFilters) {
                            // data changed (e.g. DenseArray of a pool changed) => we need to rewire to the new pointer
                            _filterData[_idx].SetData();
                        }
                        return (_entities[_idx], _filterData[_idx]);
                    } else {
                        return (_entities[_idx], default);
                    }
                }
            }

            [MethodImpl (MethodImplOptions.AggressiveInlining)]
            public bool MoveNext () {
                return ++_idx < _count;
            }

            [MethodImpl (MethodImplOptions.AggressiveInlining)]
            public void Dispose () {
                _filter.Unlock ();
            }
        }

        public struct DelayedOp {
            public bool Added;
            public int Entity;
			public int removedDenseIdx;
        }
    }
}