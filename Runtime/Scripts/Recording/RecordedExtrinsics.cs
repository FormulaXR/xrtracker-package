using System;
using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// One row of extrinsics.jsonl — camera world pose snapshotted at frame
	/// capture. Index in the array == frame index in the recorded sequence.
	/// </summary>
	[Serializable]
	public struct RecordedExtrinsics
	{
		public int FrameIndex;
		public int SchemaVersion;
		public RecordedPoseCoordinateFrame CoordinateFrame;
		public RecordedObservationStatus Status;
		public bool Valid;        // true only when pose + frame/schema validation passed
		public long TimestampNs;  // steady_clock elapsed since recording start
		public Vector3 Position;
		public Quaternion Rotation;
	}

	public enum RecordedPoseCoordinateFrame
	{
		NativeWorldFromCamera,
		UnityWorldFromCamera,
		Unsupported,
	}

	public enum RecordedObservationStatus
	{
		Valid,
		Missing,
		Invalid,
		Duplicate,
		OutOfOrder,
		WrongFrame,
		UnsupportedCoordinateFrame,
		UnsupportedSchemaVersion,
	}
}
