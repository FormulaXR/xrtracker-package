#if HAS_AR_FOUNDATION
using System;
using IV.FormulaTracker.Recording;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace IV.FormulaTracker
{
	internal enum FusionObservationSource
	{
		Live,
		Recorded,
		FaultInjected,
	}

	internal enum FusionObservationFailure
	{
		None,
		SourceUnavailable,
		MissingCameraPose,
		InvalidCameraPose,
		MissingFrame,
		DuplicateFrame,
		OutOfOrderFrame,
		WrongFrame,
		UnsupportedCoordinateFrame,
		UnsupportedSchemaVersion,
	}

	internal readonly struct FusionFrameObservation
	{
		public FusionFrameObservation(
			FusionObservationSource source,
			string sequenceId,
			int frameIndex,
			long timestampNs,
			bool hasWorldFromCamera,
			Matrix4x4 worldFromCameraUnity,
			ARSessionState sessionState,
			NotTrackingReason notTrackingReason,
			string recordingIdentity,
			string faultProfileIdentity,
			FusionObservationFailure failure,
			bool hasWorldFromSession = false,
			Matrix4x4 worldFromSessionUnity = default,
			string sessionSpaceIdentity = null,
			int sessionSpaceEpoch = 0)
		{
			Source = source;
			SequenceId = sequenceId ?? string.Empty;
			FrameIndex = frameIndex;
			TimestampNs = timestampNs;
			HasWorldFromCamera = hasWorldFromCamera;
			WorldFromCameraUnity = worldFromCameraUnity;
			SessionState = sessionState;
			NotTrackingReason = notTrackingReason;
			RecordingIdentity = recordingIdentity ?? string.Empty;
			FaultProfileIdentity = faultProfileIdentity ?? string.Empty;
			Failure = failure;
			HasWorldFromSession = hasWorldFromSession;
			WorldFromSessionUnity = worldFromSessionUnity;
			SessionSpaceIdentity = sessionSpaceIdentity ?? string.Empty;
			SessionSpaceEpoch = sessionSpaceEpoch;
		}

		public FusionObservationSource Source { get; }
		public string SequenceId { get; }
		public int FrameIndex { get; }
		public long TimestampNs { get; }
		public bool HasWorldFromCamera { get; }
		public Matrix4x4 WorldFromCameraUnity { get; }
		public ARSessionState SessionState { get; }
		public NotTrackingReason NotTrackingReason { get; }
		public string RecordingIdentity { get; }
		public string FaultProfileIdentity { get; }
		public FusionObservationFailure Failure { get; }
		public bool HasWorldFromSession { get; }
		public Matrix4x4 WorldFromSessionUnity { get; }
		public string SessionSpaceIdentity { get; }
		public int SessionSpaceEpoch { get; }

		public FusionFrameObservation WithSource(
			FusionObservationSource source,
			string faultProfileIdentity,
			FusionObservationFailure? failure = null)
		{
			return new FusionFrameObservation(
				source,
				SequenceId,
				FrameIndex,
				TimestampNs,
				HasWorldFromCamera,
				WorldFromCameraUnity,
				SessionState,
				NotTrackingReason,
				RecordingIdentity,
				faultProfileIdentity,
				failure ?? Failure,
				HasWorldFromSession,
				WorldFromSessionUnity,
				SessionSpaceIdentity,
				SessionSpaceEpoch);
		}

		public FusionFrameObservation WithFrameIndex(int frameIndex)
		{
			return new FusionFrameObservation(
				Source,
				SequenceId,
				frameIndex,
				TimestampNs,
				HasWorldFromCamera,
				WorldFromCameraUnity,
				SessionState,
				NotTrackingReason,
				RecordingIdentity,
				FaultProfileIdentity,
				Failure,
				HasWorldFromSession,
				WorldFromSessionUnity,
				SessionSpaceIdentity,
				SessionSpaceEpoch);
		}
	}

	internal readonly struct FusionFrameContext
	{
		public FusionFrameContext(
			FusionFrameObservation observation,
			FTTrackingPose nativeCameraPose,
			FusionObservationFailure failure)
		{
			Observation = observation;
			NativeCameraPose = nativeCameraPose;
			Failure = failure;
		}

		public FusionFrameObservation Observation { get; }
		public int FrameIndex => Observation.FrameIndex;
		public long TimestampNs => Observation.TimestampNs;
		/// <summary>The camera pose the step runs with, in the AR session's world frame and the engine's axes.</summary>
		public Matrix4x4 WorldFromCameraUnity => Observation.WorldFromCameraUnity;
		public FTTrackingPose NativeCameraPose { get; }
		public FusionObservationFailure Failure { get; }
		public bool IsValid => Failure == FusionObservationFailure.None;
	}

	internal interface IFusionFrameObservationSource
	{
		string Identity { get; }
		bool TryGetObservation(
			int expectedFrameIndex,
			long timestampNs,
			out FusionFrameObservation observation);
	}

	internal interface IFusionObservationFaultProfile
	{
		string Identity { get; }
		FusionFrameObservation Apply(FusionFrameObservation observation);
	}

	internal sealed class LiveFusionFrameObservationSource : IFusionFrameObservationSource
	{
		private readonly Func<Transform> _cameraAccessor;
		private readonly IARSpaceProvider _provider;

		public LiveFusionFrameObservationSource(
			Func<Transform> cameraAccessor,
			IARSpaceProvider provider)
		{
			_cameraAccessor = cameraAccessor ?? throw new ArgumentNullException(nameof(cameraAccessor));
			_provider = provider ?? throw new ArgumentNullException(nameof(provider));
		}

		public string Identity => "live";

		public bool TryGetObservation(
			int expectedFrameIndex,
			long timestampNs,
			out FusionFrameObservation observation)
		{
			Transform camera = _cameraAccessor();
			bool hasCamera = camera != null;
			bool hasSessionSpace = _provider.TryGetSessionSpace(
				out Matrix4x4 worldFromSession,
				out string sessionIdentity,
				out int sessionEpoch);

			observation = new FusionFrameObservation(
				FusionObservationSource.Live,
				string.Empty,
				expectedFrameIndex,
				timestampNs,
				hasCamera,
				hasCamera ? camera.localToWorldMatrix : Matrix4x4.identity,
				_provider.SessionState,
				_provider.NotTrackingReason,
				string.Empty,
				string.Empty,
				hasCamera
					? FusionObservationFailure.None
					: FusionObservationFailure.MissingCameraPose,
				hasSessionSpace,
				worldFromSession,
				sessionIdentity,
				sessionEpoch);
			return true;
		}
	}

	internal sealed class RecordedFusionFrameObservationSource : IFusionFrameObservationSource
	{
		private readonly SequenceReader _reader;
		private readonly PlaybackARSpaceProvider _provider;

		public RecordedFusionFrameObservationSource(
			SequenceReader reader,
			PlaybackARSpaceProvider provider)
		{
			_reader = reader ?? throw new ArgumentNullException(nameof(reader));
			_provider = provider ?? throw new ArgumentNullException(nameof(provider));
		}

		public string Identity => _reader.RecordingIdentity;

		public bool TryGetObservation(
			int expectedFrameIndex,
			long timestampNs,
			out FusionFrameObservation observation)
		{
			FusionObservationFailure failure = FusionObservationFailure.None;
			bool hasCamera = _reader.TryGetExtrinsics(expectedFrameIndex, out RecordedExtrinsics ext);
			if (!hasCamera)
			{
				failure = FusionObservationFailure.MissingFrame;
			}
			else
			{
				failure = MapFailure(ext.Status);
				hasCamera = ext.Valid && ext.FrameIndex == expectedFrameIndex &&
					failure == FusionObservationFailure.None;
			}

			Matrix4x4 worldFromCamera = Matrix4x4.identity;
			if (hasCamera)
			{
				if (ext.CoordinateFrame == RecordedPoseCoordinateFrame.NativeWorldFromCamera)
				{
					var nativePose = new FTTrackingPose
					{
						pos_x = ext.Position.x,
						pos_y = ext.Position.y,
						pos_z = ext.Position.z,
						rot_x = ext.Rotation.x,
						rot_y = ext.Rotation.y,
						rot_z = ext.Rotation.z,
						rot_w = ext.Rotation.w,
					};
					nativePose.GetTransformation(out Vector3 position, out Quaternion rotation);
					worldFromCamera = Matrix4x4.TRS(position, rotation, Vector3.one);
				}
				else
				{
					worldFromCamera = Matrix4x4.TRS(ext.Position, ext.Rotation, Vector3.one);
				}
			}

			bool hasSessionSpace = _provider.TryGetSessionSpace(
				out Matrix4x4 worldFromSession,
				out string sessionIdentity,
				out int sessionEpoch);
			observation = new FusionFrameObservation(
				FusionObservationSource.Recorded,
				_reader.SequenceId,
				expectedFrameIndex,
				hasCamera ? ext.TimestampNs : timestampNs,
				hasCamera,
				worldFromCamera,
				_provider.SessionState,
				_provider.NotTrackingReason,
				_reader.RecordingIdentity,
				string.Empty,
				failure,
				hasSessionSpace,
				worldFromSession,
				sessionIdentity,
				sessionEpoch);
			return true;
		}

		private static FusionObservationFailure MapFailure(RecordedObservationStatus status)
		{
			return status switch
			{
				RecordedObservationStatus.Valid => FusionObservationFailure.None,
				RecordedObservationStatus.Missing => FusionObservationFailure.MissingFrame,
				RecordedObservationStatus.Invalid => FusionObservationFailure.InvalidCameraPose,
				RecordedObservationStatus.Duplicate => FusionObservationFailure.DuplicateFrame,
				RecordedObservationStatus.OutOfOrder => FusionObservationFailure.OutOfOrderFrame,
				RecordedObservationStatus.WrongFrame => FusionObservationFailure.WrongFrame,
				RecordedObservationStatus.UnsupportedSchemaVersion =>
					FusionObservationFailure.UnsupportedSchemaVersion,
				RecordedObservationStatus.UnsupportedCoordinateFrame =>
					FusionObservationFailure.UnsupportedCoordinateFrame,
				_ => FusionObservationFailure.InvalidCameraPose,
			};
		}
	}

	internal sealed class FaultInjectedFusionFrameObservationSource : IFusionFrameObservationSource
	{
		private readonly IFusionFrameObservationSource _source;
		private readonly IFusionObservationFaultProfile _profile;

		public FaultInjectedFusionFrameObservationSource(
			IFusionFrameObservationSource source,
			IFusionObservationFaultProfile profile)
		{
			_source = source ?? throw new ArgumentNullException(nameof(source));
			_profile = profile ?? throw new ArgumentNullException(nameof(profile));
		}

		public string Identity => _profile.Identity;

		public bool TryGetObservation(
			int expectedFrameIndex,
			long timestampNs,
			out FusionFrameObservation observation)
		{
			if (!_source.TryGetObservation(expectedFrameIndex, timestampNs, out observation))
				return false;
			FusionFrameObservation modified = _profile.Apply(observation);
			FusionObservationFailure failure = modified.Failure;
			if (failure == FusionObservationFailure.None &&
				modified.FrameIndex != expectedFrameIndex)
			{
				failure = FusionObservationFailure.WrongFrame;
			}
			observation = modified.WithSource(
				FusionObservationSource.FaultInjected,
				_profile.Identity,
				failure);
			return true;
		}
	}

	internal sealed class FusionFrameContextResolver
	{
		public FusionFrameContext Resolve(FusionFrameObservation observation)
		{
			FusionObservationFailure failure = observation.Failure;
			if (failure == FusionObservationFailure.None && !observation.HasWorldFromCamera)
				failure = FusionObservationFailure.MissingCameraPose;

			FTTrackingPose nativePose = new FTTrackingPose { rot_w = 1f };
			if (failure == FusionObservationFailure.None)
			{
				Matrix4x4 worldFromCamera = observation.WorldFromCameraUnity;
				// The matrix is caller-supplied, so a valid observation can still carry a
				// camera pose that is not one. Installing it would put every body's solve
				// that frame against a corrupt extrinsic, so the frame is dropped instead.
				if (!ConversionUtils.IsUsablePose(worldFromCamera.GetPosition(), worldFromCamera.rotation))
					failure = FusionObservationFailure.InvalidCameraPose;
				else
					nativePose = ConversionUtils.GetConvertedPose(worldFromCamera);
			}

			return new FusionFrameContext(observation, nativePose, failure);
		}
	}

	internal interface IFusionCameraPoseSink
	{
		/// <summary>
		/// True when the camera pose reached the tracker. False means it did not,
		/// and the caller must not step: an image submitted without its own camera
		/// pose is tracked against the previous frame's.
		/// </summary>
		/// <param name="cameraPose">The camera pose for this frame.</param>
		/// <param name="frameIndex">
		/// Which frame it belongs to. The loss guard matches its observation against
		/// the frame the step consumes, so an observation carrying the wrong index is
		/// read as missing rather than applied to the wrong image.
		/// </param>
		bool Submit(FTTrackingPose cameraPose, int frameIndex);
	}

	internal sealed class NativeFusionCameraPoseSink : IFusionCameraPoseSink
	{
		public bool Submit(FTTrackingPose cameraPose, int frameIndex)
		{
			int result = FTBridge.FT_SetCameraWorldPose(ref cameraPose);
			if (result == FTErrorCode.OK)
			{
				// The same pose, handed separately to the loss guard. It is a
				// separate call because it has separate consequences: the write
				// above moves the tracking camera and changes what the solver does,
				// and this one is read by the guard alone. A body with no loss guard
				// configured makes it a no-op inside the plugin.
				FTBridge.FT_SubmitLossQuarantineObservation(frameIndex, ref cameraPose);
				return true;
			}

			// Busy is expected under contention and is not a fault; anything else is.
			// Both drop the frame, and only one of them is worth a log line.
			if (result != FTErrorCode.BUSY)
				Debug.LogWarning($"FT_SetCameraWorldPose failed with {result}; frame dropped.");
			return false;
		}
	}

	internal static class FusionCameraPoseSubmission
	{
		public static bool TrySubmit(
			FusionFrameContext context,
			IFusionCameraPoseSink sink)
		{
			if (!context.IsValid || sink == null)
				return false;
			return sink.Submit(context.NativeCameraPose, context.FrameIndex);
		}
	}

	/// <summary>
	/// AR pose fusion. Holds the AR space provider (live ARFoundation or recorded
	/// playback) that supplies the session state and the session space. A body's
	/// pose is kept in the AR session's world frame and composed with the AR camera
	/// pose every frame, so no anchor is created or read here.
	/// </summary>
	internal class ARPoseFusion
	{
		private IARSpaceProvider _provider;

		public ARPoseFusion()
		{
			_provider = new ARFoundationSpaceProvider();
		}

		/// <summary>
		/// Swap the underlying provider. Used by TrackedBodyManager to flip between
		/// live ARFoundation and recorded-session playback.
		/// </summary>
		public void SetProvider(IARSpaceProvider provider)
		{
			_provider = provider ?? new ARFoundationSpaceProvider();
		}

		/// <summary>Current provider, exposed so callers can save and restore it on a temporary swap.</summary>
		public IARSpaceProvider CurrentProvider => _provider;

		public bool IsAvailable => _provider.IsAvailable;
		public bool IsReliable => _provider.IsReliable;
		public ARSessionState SessionState => _provider.SessionState;
		public NotTrackingReason NotTrackingReason => _provider.NotTrackingReason;

		public bool Detect() => _provider.Detect();
	}
}
#endif
