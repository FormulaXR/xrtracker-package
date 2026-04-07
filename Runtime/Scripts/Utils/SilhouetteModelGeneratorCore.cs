using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Runtime-compatible silhouette model generation utilities.
	/// Used by both Editor tooling and XRProj save pipeline.
	/// </summary>
	public static class SilhouetteModelGeneratorCore
	{
		/// <summary>
		/// Generate silhouette model file from mesh data.
		/// </summary>
		public static async Task<bool> GenerateToFileAsync(
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale,
			string outputPath)
		{
			if (!MeshCombiner.Validate(meshFilters, out string error))
			{
				Debug.LogError($"[SilhouetteModelGeneratorCore] {error}");
				return false;
			}

			CombinedMeshData meshData = default;
			try
			{
				meshData = MeshCombiner.Combine(meshFilters, rootTransform);
				var modelConfig = modelSettings.ToNativeConfig(geometryScale);

				var stats = MeshCombiner.GetStats(meshFilters);
				Debug.Log($"[SilhouetteModelGeneratorCore] Generating silhouette model ({stats.meshCount} mesh(es), {stats.vertexCount} vertices)...");

				int result = await Task.Run(() => FTBridge.FT_GenerateSilhouetteModel(
					meshData.VerticesPtr, meshData.VertexCount,
					meshData.TrianglesPtr, meshData.TriangleCount,
					outputPath, ref modelConfig));

				if (result != FTErrorCode.OK)
				{
					Debug.LogError($"[SilhouetteModelGeneratorCore] Generation failed: error code {result}");
					return false;
				}

				Debug.Log($"[SilhouetteModelGeneratorCore] Saved to: {outputPath}");
				return true;
			}
			finally
			{
				meshData.Dispose();
			}
		}

		/// <summary>
		/// Generate silhouette model and return bytes directly.
		/// </summary>
		public static async Task<byte[]> GenerateToBytesAsync(
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale)
		{
			string tempPath = Path.Combine(Application.temporaryCachePath,
				$"silhouettemodel_{Guid.NewGuid()}.bin");

			try
			{
				bool success = await GenerateToFileAsync(
					meshFilters, rootTransform, modelSettings, geometryScale, tempPath);

				if (!success)
					return null;

				return await File.ReadAllBytesAsync(tempPath);
			}
			finally
			{
				if (File.Exists(tempPath))
					File.Delete(tempPath);
			}
		}

		/// <summary>
		/// Generate depth model file from mesh data.
		/// </summary>
		public static async Task<bool> GenerateDepthModelToFileAsync(
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale,
			string outputPath)
		{
			if (!MeshCombiner.Validate(meshFilters, out string error))
			{
				Debug.LogError($"[SilhouetteModelGeneratorCore] {error}");
				return false;
			}

			CombinedMeshData meshData = default;
			try
			{
				meshData = MeshCombiner.Combine(meshFilters, rootTransform);
				var modelConfig = modelSettings.ToNativeConfig(geometryScale);

				var stats = MeshCombiner.GetStats(meshFilters);
				Debug.Log($"[SilhouetteModelGeneratorCore] Generating depth model ({stats.meshCount} mesh(es), {stats.vertexCount} vertices)...");

				int result = await Task.Run(() => FTBridge.FT_GenerateDepthModel(
					meshData.VerticesPtr, meshData.VertexCount,
					meshData.TrianglesPtr, meshData.TriangleCount,
					outputPath, ref modelConfig));

				if (result != FTErrorCode.OK)
				{
					Debug.LogError($"[SilhouetteModelGeneratorCore] Depth model generation failed: error code {result}");
					return false;
				}

				Debug.Log($"[SilhouetteModelGeneratorCore] Depth model saved to: {outputPath}");
				return true;
			}
			finally
			{
				meshData.Dispose();
			}
		}

		/// <summary>
		/// Generate depth model and return bytes directly.
		/// </summary>
		public static async Task<byte[]> GenerateDepthModelToBytesAsync(
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale)
		{
			string tempPath = Path.Combine(Application.temporaryCachePath,
				$"depthmodel_{Guid.NewGuid()}.bin");

			try
			{
				bool success = await GenerateDepthModelToFileAsync(
					meshFilters, rootTransform, modelSettings, geometryScale, tempPath);

				if (!success)
					return null;

				return await File.ReadAllBytesAsync(tempPath);
			}
			finally
			{
				if (File.Exists(tempPath))
					File.Delete(tempPath);
			}
		}

		/// <summary>
		/// Generate both silhouette + depth models in a single native call.
		/// Uses a shared Vulkan device internally, avoiding the context churn
		/// that causes hangs on large (100k+ tri) meshes.
		/// Returns (silhouetteBytes, depthBytes) or (null, null) on failure.
		/// </summary>
		public static async Task<(byte[] silhouette, byte[] depth)> GenerateTrackingModelsToBytesAsync(
			IList<MeshFilter> meshFilters,
			Transform rootTransform,
			ModelSettings modelSettings,
			float geometryScale)
		{
			if (!MeshCombiner.Validate(meshFilters, out string error))
			{
				Debug.LogError($"[SilhouetteModelGeneratorCore] {error}");
				return (null, null);
			}

			string silhouetteTempPath = Path.Combine(Application.temporaryCachePath,
				$"silhouettemodel_{Guid.NewGuid()}.bin");
			string depthTempPath = Path.Combine(Application.temporaryCachePath,
				$"depthmodel_{Guid.NewGuid()}.bin");

			CombinedMeshData meshData = default;
			try
			{
				meshData = MeshCombiner.Combine(meshFilters, rootTransform);
				var modelConfig = modelSettings.ToNativeConfig(geometryScale);

				var stats = MeshCombiner.GetStats(meshFilters);
				Debug.Log($"[SilhouetteModelGeneratorCore] Generating tracking models ({stats.meshCount} mesh(es), {stats.vertexCount} vertices)...");

				int result = await Task.Run(() => FTBridge.FT_GenerateTrackingModels(
					meshData.VerticesPtr, meshData.VertexCount,
					meshData.TrianglesPtr, meshData.TriangleCount,
					silhouetteTempPath, depthTempPath, ref modelConfig));

				if (result != FTErrorCode.OK)
				{
					Debug.LogError($"[SilhouetteModelGeneratorCore] Combined model generation failed: error code {result}");
					return (null, null);
				}

				byte[] silhouetteBytes = await File.ReadAllBytesAsync(silhouetteTempPath);
				byte[] depthBytes = await File.ReadAllBytesAsync(depthTempPath);

				Debug.Log($"[SilhouetteModelGeneratorCore] Generated tracking models: silhouette={silhouetteBytes.Length} bytes, depth={depthBytes.Length} bytes");
				return (silhouetteBytes, depthBytes);
			}
			finally
			{
				meshData.Dispose();
				if (File.Exists(silhouetteTempPath))
					File.Delete(silhouetteTempPath);
				if (File.Exists(depthTempPath))
					File.Delete(depthTempPath);
			}
		}

		/// <summary>
		/// Compute a hash of the mesh data for change detection.
		/// Uses only local transforms to avoid floating point precision issues with world space.
		/// </summary>
		public static string ComputeMeshHash(IList<MeshFilter> meshFilters, Transform rootTransform)
		{
			return TrackedBodyUtils.ComputeMeshHash(meshFilters, rootTransform);
		}

		/// <summary>
		/// Check if two ModelSettings match.
		/// </summary>
		public static bool SettingsMatch(ModelSettings a, ModelSettings b)
		{
			return TrackedBodyUtils.SettingsMatch(a, b);
		}
	}
}
