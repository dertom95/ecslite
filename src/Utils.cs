using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Assertions;

public class GrowableStructArrayIndexOnly<T> where T : unmanaged {
	public T[] data;

	public int Capacity => capacity;

	[SerializeField]
	protected int capacity;
	[SerializeField]
	protected int initialCapacity;

	public int Count => count;
	[SerializeField]
	protected int count;

	public GrowableStructArrayIndexOnly(int initialCapacity) {
		Assert.IsTrue(initialCapacity > 0);

		data = new T[initialCapacity];
		capacity = initialCapacity;
		this.initialCapacity = initialCapacity;
		count = 0;
	}

	public void Add(T newData) {
		Assert.IsNotNull(data, "You need to initialize Data by using the non default construction! GrowableStructArray(capacity)");
		data[count] = newData;
		count++;
		if (count == capacity) {
			capacity *= 2;
			Array.Resize(ref data, capacity);
		}
	}

	/// <summary>
	/// moves the last element to the removed idx and decrements the count. the order is changing with this method
	/// </summary>
	/// <param name="idx"></param>
	public bool RemoveFast(int idx) {
		Assert.IsTrue(idx < count);
		int end = count - 1;
		if (idx < end) {
			data[idx] = data[end];
		}
		count--;
		return true;
	}

	/// <summary>
	/// removes index but keeps the order (by copying the array one up. still pretty fast but use RemoveFast if order doesn't matter)
	/// </summary>
	/// <param name="idx"></param>
	public void RemoveOrdered(int idx) {
		Assert.IsTrue(idx < count);
		Array.Copy(data, idx + 1, data, idx, count - idx - 1);
		count--;
	}

	public void MoveToTop(int idx) {
		Assert.IsTrue(idx < count);
		T newTopData = data[idx];
		Array.Copy(data, 0, data, 1, idx - 1);
		data[0] = newTopData;
	}

	public void Empty() {
		count = 0;
	}
}
