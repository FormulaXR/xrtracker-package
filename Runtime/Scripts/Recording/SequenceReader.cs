using System;
using System.IO;
using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// Reads recorded sequences from sequence.json + PNG frames.
	/// Editor-only — playback is for tuning in the editor, recording happens on device.
	/// </summary>
	public class SequenceReader
	{
		private readonly string _directory;
		private readonly SequenceMetadata _metadata;

		// Raw cache for fast playback (avoids PNG decode per frame)
		private string _rawCacheDir;
		private bool _hasRawCache;
		private byte[] _reusableRgbBuffer;
		private byte[] _reusableDepthByteBuffer;
		private ushort[] _reusableDepthBuffer;

		// In-memory preload (avoids per-frame disk I/O)
		private byte[] _packedColorData;
		private byte[] _packedDepthData;
		private int _colorFrameSize;
		private int _depthFrameSize;
		private bool _isPreloaded;

		// Reusable flip buffer (avoids per-frame GC allocation)
		private byte[] _flipTempRow;

		public bool IsValid { get; }
		public int FrameCount => _metadata?.frameCount ?? 0;
		public int StartIndex => _metadata?.startIndex ?? 0;
		public int ImageWidth => _metadata?.width ?? 0;
		public int ImageHeight => _metadata?.height ?? 0;

		// Depth
		public bool HasDepth => _metadata?.hasDepth ?? false;
		public float DepthScale => _metadata?.depthScale ?? 0.001f;
		public int DepthWidth => _metadata?.depthWidth ?? 0;
		public int DepthHeight => _metadata?.depthHeight ?? 0;

		/// <summary>
		/// Load a sequence from a directory containing sequence.json.
		/// </summary>
		public SequenceReader(string sequenceDirectory)
		{
			_directory = sequenceDirectory;

			string jsonPath = Path.Combine(_directory, "sequence.json");
			if (!File.Exists(jsonPath))
			{
				Debug.LogError($"[SequenceReader] sequence.json not found in: {_directory}");
				return;
			}

			string json = File.ReadAllText(jsonPath);
			_metadata = JsonUtility.FromJson<SequenceMetadata>(json);

			if (_metadata == null || _metadata.frameCount <= 0)
			{
				Debug.LogError($"[SequenceReader] Invalid metadata in: {jsonPath}");
				return;
			}

			IsValid = true;
		}

		/// <summary>
		/// Load a color frame as RGB24 byte array (flipped top-to-bottom for the native tracker).
		/// Optionally populates a display texture with the correct Unity orientation (unflipped).
		/// </summary>
		public byte[] LoadColorFrame(int index, out int width, out int height,
			Texture2D displayTarget = null)
		{
			width = 0;
			height = 0;

			string path = GetColorFramePath(index);
			if (!File.Exists(path))
			{
				Debug.LogWarning($"[SequenceReader] Color frame not found: {path}");
				return null;
			}

			byte[] fileBytes = File.ReadAllBytes(path);
			var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
			if (!tex.LoadImage(fileBytes))
			{
				SafeDestroy(tex);
				Debug.LogError($"[SequenceReader] Failed to decode: {path}");
				return null;
			}

			width = tex.width;
			height = tex.height;

			if (tex.format != TextureFormat.RGB24)
			{
				var rgb = new Texture2D(width, height, TextureFormat.RGB24, false);
				rgb.SetPixels(tex.GetPixels());
				rgb.Apply();
				SafeDestroy(tex);
				tex = rgb;
			}

			byte[] rgbData = tex.GetRawTextureData();

			// Populate display texture before flipping (correct Unity bottom-up orientation)
			if (displayTarget != null)
			{
				if (displayTarget.width != width || displayTarget.height != height)
					displayTarget.Reinitialize(width, height, TextureFormat.RGB24, false);
				displayTarget.LoadRawTextureData(rgbData);
				displayTarget.Apply();
			}

			SafeDestroy(tex);

			// Flip vertically: Unity textures are bottom-up, native tracker expects top-to-bottom
			FlipRowsInPlace(rgbData, width, height, 3);
			return rgbData;
		}

		/// <summary>
		/// Load a depth frame as uint16 array from raw binary (.bin) file.
		/// Data is stored in top-to-bottom row order, matching native tracker convention.
		/// </summary>
		public ushort[] LoadDepthFrame(int index, out int width, out int height)
		{
			width = 0;
			height = 0;

			if (!HasDepth) return null;

			string path = GetDepthFramePath(index);
			if (!File.Exists(path))
			{
				Debug.LogWarning($"[SequenceReader] Depth frame not found: {path}");
				return null;
			}

			byte[] rawData = File.ReadAllBytes(path);
			width = _metadata.depthWidth;
			height = _metadata.depthHeight;

			int expected = width * height * 2;
			if (rawData.Length != expected)
			{
				Debug.LogError($"[SequenceReader] Depth file size mismatch: expected {expected}, got {rawData.Length}");
				return null;
			}

			var depthData = new ushort[width * height];
			Buffer.BlockCopy(rawData, 0, depthData, 0, rawData.Length);
			return depthData;
		}

		/// <summary>
		/// Get intrinsics for a specific frame (normalized).
		/// Uses per-frame arrays if available, otherwise first-frame values.
		/// </summary>
		public void GetFrameIntrinsics(int index, out float fu, out float fv,
			out float ppu, out float ppv)
		{
			int arrayIndex = index - (_metadata?.startIndex ?? 0);

			if (_metadata.frameFu != null && _metadata.frameFu.Length > arrayIndex && arrayIndex >= 0)
			{
				fu = _metadata.frameFu[arrayIndex];
				fv = _metadata.frameFv[arrayIndex];
				ppu = _metadata.framePpu[arrayIndex];
				ppv = _metadata.framePpv[arrayIndex];
			}
			else
			{
				fu = _metadata.fu;
				fv = _metadata.fv;
				ppu = _metadata.ppu;
				ppv = _metadata.ppv;
			}
		}

		/// <summary>
		/// Get first-frame normalized intrinsics.
		/// </summary>
		public void GetNormalizedIntrinsics(out float fu, out float fv,
			out float ppu, out float ppv)
		{
			fu = _metadata?.fu ?? 0.9612f;
			fv = _metadata?.fv ?? 1.2817f;
			ppu = _metadata?.ppu ?? 0.5063f;
			ppv = _metadata?.ppv ?? 0.4938f;
		}

		/// <summary>
		/// Build FTCameraIntrinsics struct for tracker initialization.
		/// </summary>
		public FTCameraIntrinsics GetCameraIntrinsics()
		{
			GetNormalizedIntrinsics(out float fu, out float fv, out float ppu, out float ppv);

			return new FTCameraIntrinsics
			{
				camera_id = -1,
				width = ImageWidth,
				height = ImageHeight,
				fu_normalized = fu,
				fv_normalized = fv,
				ppu_normalized = ppu,
				ppv_normalized = ppv,
				fu_dist_normalized = fu,
				fv_dist_normalized = fv,
				ppu_dist_normalized = ppu,
				ppv_dist_normalized = ppv,
				k1 = 0, k2 = 0, p1 = 0, p2 = 0, k3 = 0
			};
		}

		/// <summary>
		/// Get depth camera extrinsics (camera2world_pose = depth-to-color transform).
		/// Returns identity if not recorded.
		/// </summary>
		public FTTrackingPose GetDepthCameraPose()
		{
			if (_metadata == null)
				return new FTTrackingPose { rot_w = 1f };

			return new FTTrackingPose
			{
				pos_x = _metadata.depthPoseX,
				pos_y = _metadata.depthPoseY,
				pos_z = _metadata.depthPoseZ,
				rot_x = _metadata.depthPoseRotX,
				rot_y = _metadata.depthPoseRotY,
				rot_z = _metadata.depthPoseRotZ,
				rot_w = _metadata.depthPoseRotW
			};
		}

		/// <summary>
		/// Check if depth camera has non-identity extrinsics (i.e., was recorded from RealSense).
		/// </summary>
		public bool HasDepthExtrinsics
		{
			get
			{
				if (_metadata == null) return false;
				// Check if pose is non-identity (any non-zero translation or non-identity rotation)
				return _metadata.depthPoseX != 0f || _metadata.depthPoseY != 0f || _metadata.depthPoseZ != 0f ||
				       _metadata.depthPoseRotX != 0f || _metadata.depthPoseRotY != 0f || _metadata.depthPoseRotZ != 0f ||
				       (_metadata.depthPoseRotW != 1f && _metadata.depthPoseRotW != 0f);
			}
		}

		/// <summary>
		/// Build FTCameraIntrinsics for the depth camera.
		/// </summary>
		public FTCameraIntrinsics GetDepthIntrinsics()
		{
			return new FTCameraIntrinsics
			{
				camera_id = -1,
				width = DepthWidth,
				height = DepthHeight,
				fu_normalized = _metadata?.depthFu ?? 0.5f,
				fv_normalized = _metadata?.depthFv ?? 0.5f,
				ppu_normalized = _metadata?.depthPpu ?? 0.5f,
				ppv_normalized = _metadata?.depthPpv ?? 0.5f,
				fu_dist_normalized = _metadata?.depthFu ?? 0.5f,
				fv_dist_normalized = _metadata?.depthFv ?? 0.5f,
				ppu_dist_normalized = _metadata?.depthPpu ?? 0.5f,
				ppv_dist_normalized = _metadata?.depthPpv ?? 0.5f,
				k1 = 0, k2 = 0, p1 = 0, p2 = 0, k3 = 0
			};
		}

		#region In-Memory Preload (zero per-frame I/O)

		public bool IsPreloaded => _isPreloaded;

		/// <summary>
		/// Preload all raw-cached frames into RAM for zero-I/O playback.
		/// Call after raw cache is created. Frames are stored pre-flipped
		/// for the native tracker (top-to-bottom) so no flip needed at play time.
		/// </summary>
		public void PreloadAllFrames(Action<float, string> onProgress = null)
		{
			if (!HasRawCache || _metadata == null) return;

			int count = _metadata.frameCount;
			int start = _metadata.startIndex;
			_colorFrameSize = _metadata.width * _metadata.height * 3;

			long totalColorBytes = (long)count * _colorFrameSize;
			if (totalColorBytes > int.MaxValue)
			{
				Debug.LogWarning($"[SequenceReader] Sequence too large for preload " +
				                 $"({totalColorBytes / (1024 * 1024)}MB). Using disk I/O.");
				return;
			}

			onProgress?.Invoke(0f, "Preloading color frames into RAM...");
			_packedColorData = new byte[count * _colorFrameSize];

			for (int i = 0; i < count; i++)
			{
				string rawPath = Path.Combine(_rawCacheDir, $"{start + i}.rgb");
				if (!File.Exists(rawPath)) continue;

				using (var fs = File.OpenRead(rawPath))
					fs.Read(_packedColorData, i * _colorFrameSize, _colorFrameSize);

				// Pre-flip for native tracker so no flip needed during playback
				FlipRowsInBuffer(_packedColorData, i * _colorFrameSize,
					_metadata.width, _metadata.height, 3);

				if (i % 50 == 0)
					onProgress?.Invoke((float)i / count, $"Preloading frame {i}/{count}...");
			}

			// Preload depth frames if available
			if (HasDepth)
			{
				_depthFrameSize = _metadata.depthWidth * _metadata.depthHeight * 2;
				long totalDepthBytes = (long)count * _depthFrameSize;
				if (totalDepthBytes <= int.MaxValue)
				{
					onProgress?.Invoke(0.9f, "Preloading depth frames...");
					_packedDepthData = new byte[count * _depthFrameSize];

					for (int i = 0; i < count; i++)
					{
						string path = GetDepthFramePath(start + i);
						if (!File.Exists(path)) continue;
						using (var fs = File.OpenRead(path))
							fs.Read(_packedDepthData, i * _depthFrameSize, _depthFrameSize);
					}
				}
			}

			_isPreloaded = true;
			onProgress?.Invoke(1f, "Preload complete");
			Debug.Log($"[SequenceReader] Preloaded {count} frames into RAM " +
			          $"({totalColorBytes / (1024 * 1024)}MB color" +
			          (_packedDepthData != null ? $" + {(long)count * _depthFrameSize / (1024 * 1024)}MB depth" : "") +
			          ")");
		}

		/// <summary>
		/// Flip rows within a region of a larger buffer (no allocation).
		/// </summary>
		private void FlipRowsInBuffer(byte[] data, int baseOffset, int width, int height, int bpp)
		{
			int rowBytes = width * bpp;
			if (_flipTempRow == null || _flipTempRow.Length < rowBytes)
				_flipTempRow = new byte[rowBytes];

			for (int y = 0; y < height / 2; y++)
			{
				int topOffset = baseOffset + y * rowBytes;
				int bottomOffset = baseOffset + (height - 1 - y) * rowBytes;
				Buffer.BlockCopy(data, topOffset, _flipTempRow, 0, rowBytes);
				Buffer.BlockCopy(data, bottomOffset, data, topOffset, rowBytes);
				Buffer.BlockCopy(_flipTempRow, 0, data, bottomOffset, rowBytes);
			}
		}

		#endregion

		#region Raw Cache (fast playback — avoids per-frame PNG decode)

		/// <summary>
		/// Whether a raw cache directory with all converted frames exists.
		/// </summary>
		public bool HasRawCache
		{
			get
			{
				if (_rawCacheDir == null)
					_rawCacheDir = Path.Combine(_directory, "raw");
				return _hasRawCache || (_hasRawCache = File.Exists(
					Path.Combine(_rawCacheDir, ".complete")));
			}
		}

		/// <summary>
		/// One-time conversion: decode all PNGs to raw RGB24 binary files.
		/// Stores in Unity orientation (bottom-up) so display texture needs no flip.
		/// Call FlipRowsInPlace on the loaded data for the native tracker.
		/// </summary>
		/// <param name="onProgress">Optional progress callback (0-1, message).</param>
		public void CreateRawCache(Action<float, string> onProgress = null)
		{
			_rawCacheDir = Path.Combine(_directory, "raw");
			Directory.CreateDirectory(_rawCacheDir);

			int start = _metadata.startIndex;
			int count = _metadata.frameCount;

			for (int i = 0; i < count; i++)
			{
				int frameIdx = start + i;
				onProgress?.Invoke((float)i / count, $"Converting frame {frameIdx}/{start + count - 1}...");

				string pngPath = GetColorFramePath(frameIdx);
				if (!File.Exists(pngPath)) continue;

				byte[] fileBytes = File.ReadAllBytes(pngPath);
				var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
				if (!tex.LoadImage(fileBytes))
				{
					SafeDestroy(tex);
					continue;
				}

				int w = tex.width;
				int h = tex.height;

				if (tex.format != TextureFormat.RGB24)
				{
					var rgb = new Texture2D(w, h, TextureFormat.RGB24, false);
					rgb.SetPixels(tex.GetPixels());
					rgb.Apply();
					SafeDestroy(tex);
					tex = rgb;
				}

				byte[] rgbData = tex.GetRawTextureData();
				SafeDestroy(tex);

				// Store in Unity orientation (bottom-up) — no flip.
				// FlipRowsInPlace is applied at load time for the tracker only.
				string rawPath = Path.Combine(_rawCacheDir, $"{frameIdx}.rgb");
				File.WriteAllBytes(rawPath, rgbData);
			}

			File.WriteAllText(Path.Combine(_rawCacheDir, ".complete"),
				$"{count} frames, {_metadata.width}x{_metadata.height}");
			_hasRawCache = true;

			onProgress?.Invoke(1f, "Done");
			Debug.Log($"[SequenceReader] Raw cache created: {count} frames in {_rawCacheDir}");
		}

		/// <summary>
		/// Fast frame loading from raw cache. No PNG decode, no Texture2D allocation.
		/// Returns RGB24 data flipped for the native tracker (top-to-bottom).
		/// Reuses internal buffer to avoid GC allocations.
		/// </summary>
		public byte[] LoadColorFrameFast(int index, out int width, out int height,
			Texture2D displayTarget = null)
		{
			width = _metadata.width;
			height = _metadata.height;
			int expectedSize = width * height * 3;

			// Reuse buffer to avoid per-frame GC allocation
			if (_reusableRgbBuffer == null || _reusableRgbBuffer.Length != expectedSize)
				_reusableRgbBuffer = new byte[expectedSize];

			// Fast path: serve from preloaded RAM (already pre-flipped for native tracker)
			if (_isPreloaded && _packedColorData != null)
			{
				int frameIdx = index - _metadata.startIndex;
				int offset = frameIdx * _colorFrameSize;
				if (offset >= 0 && offset + _colorFrameSize <= _packedColorData.Length)
				{
					Buffer.BlockCopy(_packedColorData, offset, _reusableRgbBuffer, 0, _colorFrameSize);

					// Display texture needs Unity orientation (bottom-up) — flip the copy
					if (displayTarget != null)
					{
						if (displayTarget.width != width || displayTarget.height != height)
							displayTarget.Reinitialize(width, height, TextureFormat.RGB24, false);
						// Flip back for display, then load
						FlipRowsInPlace(_reusableRgbBuffer, width, height, 3);
						displayTarget.LoadRawTextureData(_reusableRgbBuffer);
						displayTarget.Apply();
						// Re-flip for native tracker
						FlipRowsInPlace(_reusableRgbBuffer, width, height, 3);
					}

					return _reusableRgbBuffer;
				}
			}

			// Disk fallback: read from raw cache file
			string rawPath = Path.Combine(_rawCacheDir, $"{index}.rgb");
			if (!File.Exists(rawPath))
				return LoadColorFrame(index, out width, out height, displayTarget);

			using (var fs = File.OpenRead(rawPath))
			{
				int bytesRead = 0;
				while (bytesRead < expectedSize)
				{
					int n = fs.Read(_reusableRgbBuffer, bytesRead, expectedSize - bytesRead);
					if (n <= 0) break;
					bytesRead += n;
				}
			}

			// Display texture — raw cache is in Unity orientation (bottom-up), no flip needed
			if (displayTarget != null)
			{
				if (displayTarget.width != width || displayTarget.height != height)
					displayTarget.Reinitialize(width, height, TextureFormat.RGB24, false);
				displayTarget.LoadRawTextureData(_reusableRgbBuffer);
				displayTarget.Apply();
			}

			// Flip for native tracker (top-to-bottom)
			FlipRowsInPlace(_reusableRgbBuffer, width, height, 3);
			return _reusableRgbBuffer;
		}

		/// <summary>
		/// Fast depth frame loading with reusable buffer.
		/// </summary>
		public ushort[] LoadDepthFrameFast(int index, out int width, out int height)
		{
			width = 0;
			height = 0;
			if (!HasDepth) return null;

			width = _metadata.depthWidth;
			height = _metadata.depthHeight;
			int pixelCount = width * height;
			int expectedBytes = pixelCount * 2;

			if (_reusableDepthBuffer == null || _reusableDepthBuffer.Length != pixelCount)
				_reusableDepthBuffer = new ushort[pixelCount];

			// Fast path: serve from preloaded RAM
			if (_isPreloaded && _packedDepthData != null)
			{
				int frameIdx = index - _metadata.startIndex;
				int offset = frameIdx * _depthFrameSize;
				if (offset >= 0 && offset + _depthFrameSize <= _packedDepthData.Length)
				{
					Buffer.BlockCopy(_packedDepthData, offset, _reusableDepthBuffer, 0, expectedBytes);
					return _reusableDepthBuffer;
				}
			}

			// Disk fallback
			string path = GetDepthFramePath(index);
			if (!File.Exists(path)) return null;

			if (_reusableDepthByteBuffer == null || _reusableDepthByteBuffer.Length != expectedBytes)
				_reusableDepthByteBuffer = new byte[expectedBytes];

			using (var fs = File.OpenRead(path))
			{
				int bytesRead = 0;
				while (bytesRead < expectedBytes)
				{
					int n = fs.Read(_reusableDepthByteBuffer, bytesRead, expectedBytes - bytesRead);
					if (n <= 0) break;
					bytesRead += n;
				}
			}

			Buffer.BlockCopy(_reusableDepthByteBuffer, 0, _reusableDepthBuffer, 0, expectedBytes);
			return _reusableDepthBuffer;
		}

		#endregion

		private static void FlipDepthRows(ushort[] data, int width, int height)
		{
			var tempRow = new ushort[width];
			for (int y = 0; y < height / 2; y++)
			{
				int topOffset = y * width;
				int bottomOffset = (height - 1 - y) * width;
				Array.Copy(data, topOffset, tempRow, 0, width);
				Array.Copy(data, bottomOffset, data, topOffset, width);
				Array.Copy(tempRow, 0, data, bottomOffset, width);
			}
		}

		private static void FlipRowsInPlace(byte[] data, int width, int height, int bytesPerPixel)
		{
			int rowBytes = width * bytesPerPixel;
			var tempRow = new byte[rowBytes];
			for (int y = 0; y < height / 2; y++)
			{
				int topOffset = y * rowBytes;
				int bottomOffset = (height - 1 - y) * rowBytes;
				Buffer.BlockCopy(data, topOffset, tempRow, 0, rowBytes);
				Buffer.BlockCopy(data, bottomOffset, data, topOffset, rowBytes);
				Buffer.BlockCopy(tempRow, 0, data, bottomOffset, rowBytes);
			}
		}

		private string GetColorFramePath(int index)
		{
			string prefix = _metadata?.imagePrefix ?? "color_camera_image_";
			string ext = _metadata?.imageType ?? "png";
			return Path.Combine(_directory, $"{prefix}{index}.{ext}");
		}

		private string GetDepthFramePath(int index)
		{
			string prefix = _metadata?.depthPrefix ?? "depth_camera_image_";
			return Path.Combine(_directory, $"{prefix}{index}.bin");
		}

		private static void SafeDestroy(UnityEngine.Object obj)
		{
			if (obj == null) return;
#if UNITY_EDITOR
			if (!Application.isPlaying)
				UnityEngine.Object.DestroyImmediate(obj);
			else
#endif
				UnityEngine.Object.Destroy(obj);
		}
	}
}