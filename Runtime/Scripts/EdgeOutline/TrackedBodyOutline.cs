#if HAS_URP
using System;
using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Edge outline renderer that uses the same mesh sources as a TrackedBody.
	/// Attach to a GameObject that has a TrackedBody component.
	/// </summary>
	[RequireComponent(typeof(TrackedBody))]
	public class TrackedBodyOutline : EdgeOutlineRenderer
	{
		TrackedBody _body;

		protected override IList<MeshFilter> CollectMeshFilters()
		{
			if (_body == null)
				_body = GetComponent<TrackedBody>();
			if (_body == null)
				return Array.Empty<MeshFilter>();

			// Use only the meshes explicitly assigned to the TrackedBody,
			// not all meshes in the hierarchy.
			return _body.MeshFilters;
		}
	}
}
#endif
