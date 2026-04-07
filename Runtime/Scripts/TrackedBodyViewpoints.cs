using System;
using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Utility component for managing named viewpoints for a TrackedBody.
	/// Viewpoints represent different camera positions from which the object can be initialized.
	/// </summary>
	public class TrackedBodyViewpoints : MonoBehaviour
	{
		[Serializable]
		public class NamedViewpoint
		{
			[SerializeField] public string _name;
			[SerializeField] public Transform _transform;
		}

		[SerializeField] private TrackedBody _trackedBody;

		[SerializeField] private List<NamedViewpoint> _viewpoints = new();

		/// <summary>
		/// The TrackedBody this component controls.
		/// </summary>
		public TrackedBody TrackedBody
		{
			get => _trackedBody;
			set => _trackedBody = value;
		}

		/// <summary>
		/// List of named viewpoints.
		/// </summary>
		public IReadOnlyList<NamedViewpoint> Viewpoints => _viewpoints;

		/// <summary>
		/// Number of viewpoints.
		/// </summary>
		public int Count => _viewpoints.Count;

		private void Reset()
		{
			_trackedBody = GetComponent<TrackedBody>();
		}

		private void OnValidate()
		{
			foreach (NamedViewpoint viewpoint in _viewpoints)
			{
				if (string.IsNullOrWhiteSpace(viewpoint._name) && viewpoint._transform != null)
					viewpoint._name = viewpoint._transform.name;
			}
		}

		/// <summary>
		/// Get a viewpoint by name.
		/// </summary>
		public Transform GetViewpoint(string viewpointName)
		{
			foreach (var vp in _viewpoints)
			{
				if (string.Equals(vp._name, viewpointName, StringComparison.OrdinalIgnoreCase))
					return vp._transform;
			}

			return null;
		}

		/// <summary>
		/// Get a viewpoint by index.
		/// </summary>
		public Transform GetViewpoint(int index)
		{
			if (index < 0 || index >= _viewpoints.Count)
				return null;
			return _viewpoints[index]._transform;
		}

		/// <summary>
		/// Set the initial pose from a named viewpoint.
		/// </summary>
		/// <param name="viewpointName">Name of the viewpoint</param>
		/// <returns>True if viewpoint was found and pose was set</returns>
		public bool SetInitialPoseFromViewpoint(string viewpointName)
		{
			Transform vp = GetViewpoint(viewpointName);
			if (vp == null)
			{
				Debug.LogWarning($"[TrackedBodyViewpoints] Viewpoint '{viewpointName}' not found");
				return false;
			}

			return SetInitialPoseFromViewpoint(vp);
		}

		/// <summary>
		/// Set the initial pose from a viewpoint by index.
		/// </summary>
		/// <param name="index">Index of the viewpoint</param>
		/// <returns>True if viewpoint was found and pose was set</returns>
		public bool SetInitialPoseFromViewpoint(int index)
		{
			var vp = GetViewpoint(index);
			if (vp == null)
			{
				Debug.LogWarning($"[TrackedBodyViewpoints] Viewpoint at index {index} not found");
				return false;
			}

			return SetInitialPoseFromViewpoint(vp);
		}

		/// <summary>
		/// Set the initial pose from a viewpoint transform.
		/// </summary>
		/// <param name="viewpoint">The viewpoint transform</param>
		/// <returns>True if pose was set</returns>
		public bool SetInitialPoseFromViewpoint(Transform viewpoint)
		{
			if (_trackedBody == null)
			{
				Debug.LogError("[TrackedBodyViewpoints] TrackedBody reference is not set");
				return false;
			}

			if (viewpoint == null)
			{
				Debug.LogWarning("[TrackedBodyViewpoints] Viewpoint transform is null");
				return false;
			}

			_trackedBody.SetInitialPose(viewpoint);
			return true;
		}

		/// <summary>
		/// Add a viewpoint at runtime.
		/// </summary>
		public void AddViewpoint(string viewpointName, Transform viewpointTransform)
		{
			_viewpoints.Add(new NamedViewpoint { _name = viewpointName, _transform = viewpointTransform });
		}

		/// <summary>
		/// Remove a viewpoint by name.
		/// </summary>
		public bool RemoveViewpoint(string viewpointName)
		{
			for (int i = _viewpoints.Count - 1; i >= 0; i--)
			{
				if (string.Equals(_viewpoints[i]._name, viewpointName, StringComparison.OrdinalIgnoreCase))
				{
					_viewpoints.RemoveAt(i);
					return true;
				}
			}

			return false;
		}
	}
}