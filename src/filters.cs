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
		internal bool dataChanged = false;
		/// <summary>
		/// DEPRECATED! Use EnableInOut() => and its HasChanged-Property. This will make sure the changes get recognized on later frames as well. Filter's DataChanged will be cleared after each frame
		/// </summary>
		public bool DataChanged => dataChanged;
		public void Reset() => dataChanged = false;

		private bool destroyed;
		public bool Destroyed => destroyed;

		public virtual void Destroy() {
			destroyed = true;
			SparseEntities = null;
		}

#if EZ_SANITY_CHECK
		public string name;

		public void DEBUG_SetName(string filterName) {
			this.name = filterName;
		}
#endif
	}

	public class FilterInOutData {
		public HashSet<int> added;
		public HashSet<int> removed;
		public bool HasChanges => added.Count > 0 || removed.Count > 0;

		public FilterInOutData(int capacity) {
			added = new HashSet<int>(capacity);
			removed = new HashSet<int>(capacity);
		}

		internal void AddedEntity(int entity,bool forceAdd=false) {
			// only add to added if it wasn't in removed already
			if (forceAdd || !removed.Remove(entity)) {
				added.Add(entity);
			} 
		}

		internal void RemovedEntity(int entity,bool forceAdd=false) {
			// only add to removed if it wasn't in added already
			if (forceAdd || !added.Remove(entity)) {
				removed.Add(entity);
			} 
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasEntitiesInNewQueue() {
			return added.Count > 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool HasEntitiesInRemovedQueue() {
			return removed.Count > 0;
		}

		
		/// <summary>
		/// Clear queue
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void ClearAddedQueue() {
			added.Clear();
		}

		/// <summary>
		/// Clear queue
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void ClearQueues() {
			added.Clear();
			removed.Clear();
		}

		/// <summary>
		/// Clear queue
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void ClearRemovedQueue() {
			removed.Clear();
		}

		public void Destroy() {
			ClearQueues();
			added = null;
			removed = null;
		}

	}

	public sealed class EcsFilter<T> : EcsFilter where T:IFilterData {
        readonly EcsWorld _world;
        EcsWorld.Mask _mask;
        int[] _denseEntities;
        T[] _filterData=null;
        int _entitiesCount;
        int _lockCount;
        DelayedOp[] _delayedOps;
        int _delayedOpsCount;



		bool usingInOutMechanism;
		List<FilterInOutData> allInOutData;

#if LEOECSLITE_FILTER_EVENTS
        IEcsFilterEventListener[] _eventListeners = new IEcsFilterEventListener[4];
        int _eventListenersCount;

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

		public override void Destroy() {
			base.Destroy();

			_denseEntities = null;
			_delayedOps = null;
			_filterData = null;
			_mask = null;
			
			if (allInOutData != null) {
				foreach (FilterInOutData fio in allInOutData) {
					fio.Destroy();
				}
			}
			allInOutData = null;

		}

		[MethodImpl (MethodImplOptions.AggressiveInlining)]
        public EcsWorld GetWorld () {
            return _world;
        }

		/// <summary>
		/// When enabling the inOutQueue! It is important not to forget to dequeue both queues. Otherwise the system will throw asserts
		/// CAUTION: DO NOT FORGET TO CLEAR THE QUEUES (ClearQueues(),ClearAddedQueue(),ClearRemovedQueue())
		/// </summary>
		public FilterInOutData EnableInOut(bool fillAddedWithCurrentData=false) {
			if (allInOutData==null) {
				allInOutData = new List<FilterInOutData>();
			}
			FilterInOutData filterInOutData = new FilterInOutData(10);
			allInOutData.Add(filterInOutData);
			usingInOutMechanism = true;

			if (fillAddedWithCurrentData) {
				foreach ((int entity,_) in this) {
					filterInOutData.AddedEntity(entity, true); // add without checking the removed-queue
				}
			}

			return filterInOutData;
		}

		public void DisableInOutQueue(FilterInOutData inOutData) {
			Assert.IsNotNull(inOutData);
			Assert.IsNotNull(allInOutData);
			allInOutData.Remove(inOutData);
			if (allInOutData.Count == 0) {
				allInOutData = null;
			}
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

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int GetRandomEntity() {
			int randomEntity = _denseEntities[UnityEngine.Random.Range(0,_entitiesCount)];
			return randomEntity;
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
			dataChanged = true;
			if (usingInOutMechanism) {
				for (int i = 0, iEnd = allInOutData.Count; i < iEnd; i++) {
					FilterInOutData inOutData = allInOutData[i];
					inOutData.AddedEntity(packedEntity);
				}
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
			if (usingInOutMechanism) {
				int packedEntity = _world.PackEntity(entity);
				for (int i = 0, iEnd = allInOutData.Count; i < iEnd; i++) {
					FilterInOutData inOutData = allInOutData[i];
					inOutData.RemovedEntity(packedEntity);
				}
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
			dataChanged = true;

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
				_filter.updateFilters = false;
            }
        }

        public struct DelayedOp {
            public bool Added;
            public int Entity;
			public int removedDenseIdx;
        }
    }
}