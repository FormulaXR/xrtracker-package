using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using IV.FormulaTracker;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Immutable transform and intrinsics snapshot shared by rendering, native
	/// frame submission, and optional truth output for one Scene View frame.
	/// Poses use destination-from-source names and meters.
	/// </summary>
	internal readonly struct SceneViewFrameSnapshot
	{
		public readonly long TimestampNs;
		public readonly int Width;
		public readonly int Height;
		public readonly float Fu;
		public readonly float Fv;
		public readonly float Ppu;
		public readonly float Ppv;
		public readonly Matrix4x4 WorldFromCameraUnity;
		public readonly FTTrackingPose WorldFromCameraNative;
		public readonly bool HasTruthBody;
		public readonly Matrix4x4 WorldFromBodyUnity;
		public readonly Matrix4x4 CameraFromBodyUnity;
		public readonly FTTrackingPose WorldFromBodyNative;
		public readonly FTTrackingPose CameraFromBodyNative;

		private SceneViewFrameSnapshot(
			long timestampNs,
			int width,
			int height,
			float fu,
			float fv,
			float ppu,
			float ppv,
			Matrix4x4 worldFromCameraUnity,
			Matrix4x4? worldFromBodyUnity)
		{
			TimestampNs = timestampNs;
			Width = width;
			Height = height;
			Fu = fu;
			Fv = fv;
			Ppu = ppu;
			Ppv = ppv;
			WorldFromCameraUnity = worldFromCameraUnity;
			WorldFromCameraNative = ConversionUtils.GetConvertedPose(worldFromCameraUnity);

			HasTruthBody = worldFromBodyUnity.HasValue;
			WorldFromBodyUnity = worldFromBodyUnity ?? Matrix4x4.identity;
			CameraFromBodyUnity = HasTruthBody
				? worldFromCameraUnity.inverse * WorldFromBodyUnity
				: Matrix4x4.identity;
			WorldFromBodyNative = HasTruthBody
				? ConversionUtils.GetConvertedPose(WorldFromBodyUnity)
				: default;
			CameraFromBodyNative = HasTruthBody
				? ConversionUtils.GetConvertedPose(CameraFromBodyUnity)
				: default;
		}

		public static SceneViewFrameSnapshot Create(
			long timestampNs,
			int width,
			int height,
			float fu,
			float fv,
			float ppu,
			float ppv,
			Matrix4x4 worldFromCameraUnity,
			Matrix4x4? worldFromBodyUnity = null)
		{
			return new SceneViewFrameSnapshot(
				timestampNs, width, height, fu, fv, ppu, ppv,
				worldFromCameraUnity, worldFromBodyUnity);
		}
	}

	internal static class SceneViewRecordingUtilities
	{
		/// <summary>
		/// Finalize a prewritten truth row only when native committed the matching
		/// color image and extrinsics row. Rejected native submissions truncate the
		/// pending truth row before returning.
		/// </summary>
		internal static bool TryFinalizeSubmittedFrame(
			int nativeResult,
			int currentFrameCount,
			SceneViewTruthWriter truthWriter,
			out int nextFrameCount,
			out string error)
		{
			nextFrameCount = currentFrameCount;
			if (nativeResult != FTErrorCode.OK)
			{
				try
				{
					truthWriter?.RollbackFrame();
					error = $"FT_RecordFrameWithCameraPose failed: {nativeResult}";
					return false;
				}
				catch (Exception exception)
				{
					error = $"Native submission failed ({nativeResult}) and truth rollback failed: {exception.Message}";
					return false;
				}
			}

			try
			{
				truthWriter?.CommitFrame();
				nextFrameCount = currentFrameCount + 1;
				error = null;
				return true;
			}
			catch (Exception exception)
			{
				error = $"truth.jsonl commit failed for frame {currentFrameCount}: {exception.Message}";
				return false;
			}
		}

		/// <summary>
		/// Projects a native camera-space point to normalized top-left image
		/// coordinates using normalized pinhole intrinsics.
		/// </summary>
		internal static Vector2 ProjectNativeCameraPoint(
			Vector3 cameraPointNative,
			float fu,
			float fv,
			float ppu,
			float ppv)
		{
			if (cameraPointNative.z <= 0f)
				throw new ArgumentOutOfRangeException(nameof(cameraPointNative), "Point must be in front of the camera.");

			return new Vector2(
				fu * cameraPointNative.x / cameraPointNative.z + ppu,
				fv * cameraPointNative.y / cameraPointNative.z + ppv);
		}

		internal static bool NativePosesApproximatelyEqual(
			FTTrackingPose expected,
			FTTrackingPose actual,
			float positionTolerance,
			float rotationToleranceDegrees)
		{
			var expectedPosition = new Vector3(expected.pos_x, expected.pos_y, expected.pos_z);
			var actualPosition = new Vector3(actual.pos_x, actual.pos_y, actual.pos_z);
			if (Vector3.Distance(expectedPosition, actualPosition) > positionTolerance)
				return false;

			var expectedRotation = new Quaternion(expected.rot_x, expected.rot_y, expected.rot_z, expected.rot_w);
			var actualRotation = new Quaternion(actual.rot_x, actual.rot_y, actual.rot_z, actual.rot_w);
			return Quaternion.Angle(expectedRotation, actualRotation) <= rotationToleranceDegrees;
		}
	}

	internal sealed class SceneViewTruthWriter : IDisposable
	{
		private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
		private readonly StreamWriter _writer;
		private long _pendingStart = -1;

		public string Path { get; }

		public SceneViewTruthWriter(string outputDirectory)
		{
			if (string.IsNullOrWhiteSpace(outputDirectory))
				throw new ArgumentException("Output directory is required.", nameof(outputDirectory));

			Directory.CreateDirectory(outputDirectory);
			Path = System.IO.Path.Combine(outputDirectory, "truth.jsonl");
			_writer = new StreamWriter(Path, false, new UTF8Encoding(false));
		}

		internal void BeginFrame(int frameIndex, SceneViewFrameSnapshot snapshot)
		{
			if (!snapshot.HasTruthBody)
				throw new InvalidOperationException("A truth body snapshot is required.");
			if (_pendingStart >= 0)
				throw new InvalidOperationException("A truth frame transaction is already pending.");

			_writer.Flush();
			_pendingStart = _writer.BaseStream.Position;

			var json = new StringBuilder(1024);
			json.Append("{\"frame\":").Append(frameIndex);
			json.Append(",\"ts_ns\":").Append(snapshot.TimestampNs);
			json.Append(",\"width\":").Append(snapshot.Width);
			json.Append(",\"height\":").Append(snapshot.Height);
			AppendFloat(json, "fu", snapshot.Fu);
			AppendFloat(json, "fv", snapshot.Fv);
			AppendFloat(json, "ppu", snapshot.Ppu);
			AppendFloat(json, "ppv", snapshot.Ppv);
			AppendUnityPose(json, "world_from_camera_unity", snapshot.WorldFromCameraUnity);
			AppendUnityPose(json, "world_from_body_unity", snapshot.WorldFromBodyUnity);
			AppendUnityPose(json, "camera_from_body_unity", snapshot.CameraFromBodyUnity);
			AppendNativePose(json, "world_from_camera_native", snapshot.WorldFromCameraNative);
			AppendNativePose(json, "world_from_body_native", snapshot.WorldFromBodyNative);
			AppendNativePose(json, "camera_from_body_native", snapshot.CameraFromBodyNative);
			json.Append('}');

			_writer.WriteLine(json.ToString());
			_writer.Flush();
		}

		public void CommitFrame()
		{
			if (_pendingStart < 0)
				return;
			_pendingStart = -1;
		}

		public void RollbackFrame()
		{
			if (_pendingStart < 0)
				return;
			_writer.Flush();
			_writer.BaseStream.SetLength(_pendingStart);
			_writer.BaseStream.Position = _pendingStart;
			_pendingStart = -1;
		}

		public void Dispose()
		{
			RollbackFrame();
			_writer.Dispose();
		}

		private static void AppendFloat(StringBuilder json, string name, float value)
		{
			json.Append(",\"").Append(name).Append("\":");
			json.Append(value.ToString("R", Invariant));
		}

		internal static void AppendUnityPose(StringBuilder json, string name, Matrix4x4 matrix)
		{
			AppendPose(json, name, matrix.GetPosition(), matrix.rotation);
		}

		internal static void AppendNativePose(StringBuilder json, string name, FTTrackingPose pose)
		{
			AppendPose(
				json,
				name,
				new Vector3(pose.pos_x, pose.pos_y, pose.pos_z),
				new Quaternion(pose.rot_x, pose.rot_y, pose.rot_z, pose.rot_w));
		}

		private static void AppendPose(StringBuilder json, string name, Vector3 position, Quaternion rotation)
		{
			json.Append(",\"").Append(name).Append("\":{\"t\":[");
			AppendValue(json, position.x);
			json.Append(',');
			AppendValue(json, position.y);
			json.Append(',');
			AppendValue(json, position.z);
			json.Append("],\"q\":[");
			AppendValue(json, rotation.x);
			json.Append(',');
			AppendValue(json, rotation.y);
			json.Append(',');
			AppendValue(json, rotation.z);
			json.Append(',');
			AppendValue(json, rotation.w);
			json.Append("]}");
		}

		private static void AppendValue(StringBuilder json, float value)
		{
			json.Append(value.ToString("R", Invariant));
		}
	}
}
