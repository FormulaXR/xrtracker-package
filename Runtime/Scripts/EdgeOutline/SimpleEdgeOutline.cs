#if HAS_URP
using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Edge outline renderer for standalone use.
	/// Works on this GameObject's own MeshFilter, optionally including children.
	/// </summary>
	public class SimpleEdgeOutline : EdgeOutlineRenderer
	{
		[Header("Mesh Source")]
		[Tooltip("Include MeshFilters from child GameObjects.")]
		[SerializeField] bool _includeChildren;

		protected override IList<MeshFilter> CollectMeshFilters()
		{
			if (_includeChildren)
				return GetComponentsInChildren<MeshFilter>();

			var mf = GetComponent<MeshFilter>();
			return mf != null ? new[] { mf } : System.Array.Empty<MeshFilter>();
		}

	}
}
#endif
