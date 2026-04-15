using System;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// JSON-serializable metadata for a recorded camera sequence.
	/// Stores color intrinsics (normalized), optional per-frame intrinsics,
	/// and optional depth camera info.
	/// </summary>
	[Serializable]
	public class SequenceMetadata
	{
		// Color camera
		public string cameraName = "color_camera";
		public string imagePrefix = "color_camera_image_";
		public string imageType = "png";
		public int startIndex;
		public int frameCount;
		public int width;
		public int height;

		// First-frame color intrinsics (normalized: fu = fx_pixels / width)
		public float fu;
		public float fv;
		public float ppu;
		public float ppv;

		// Per-frame color intrinsics (null if constant, e.g. iOS autofocus)
		public float[] frameFu;
		public float[] frameFv;
		public float[] framePpu;
		public float[] framePpv;

		// Depth camera
		public bool hasDepth;
		public string depthPrefix = "depth_camera_image_";
		public float depthScale = 0.001f;
		public int depthWidth;
		public int depthHeight;
		public float depthFu;
		public float depthFv;
		public float depthPpu;
		public float depthPpv;

		// Depth camera extrinsics (camera2world_pose = depth-to-color transform)
		// Required for RealSense recordings where depth sensor is physically offset from color sensor
		public float depthPoseX;
		public float depthPoseY;
		public float depthPoseZ;
		public float depthPoseRotX;
		public float depthPoseRotY;
		public float depthPoseRotZ;
		public float depthPoseRotW = 1f;
	}
}
