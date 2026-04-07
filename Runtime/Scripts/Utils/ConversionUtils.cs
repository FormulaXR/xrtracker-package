using UnityEngine;

namespace IV.FormulaTracker
{
	public static class ConversionUtils
	{
		private static readonly Matrix4x4 InvertedY = Matrix4x4.Scale(new Vector3(1, -1, 1));

		internal static Matrix4x4 ConvertMatrix(Matrix4x4 input)
		{
			return InvertedY * input * InvertedY;
		}

		internal static void GetTransformation(this FTTrackingPose pose, out Vector3 position, out Quaternion rotation)
		{
			var posePosition = new Vector3(pose.pos_x, pose.pos_y, pose.pos_z);
			var poseRotation = new Quaternion(pose.rot_x, pose.rot_y, pose.rot_z, pose.rot_w);
			Matrix4x4 matrix = Matrix4x4.TRS(posePosition, poseRotation, Vector3.one);
			matrix = ConvertMatrix(matrix);
			position = matrix.GetPosition();
			rotation = matrix.rotation;
		}

		internal static FTTrackingPose GetConvertedPose(this Matrix4x4 matrix)
		{
			matrix = ConvertMatrix(matrix);
			Vector3 position = matrix.GetPosition();
			Quaternion rotation = matrix.rotation;

			return new FTTrackingPose
			{
				pos_x = position.x,
				pos_y = position.y,
				pos_z = position.z,
				rot_x = rotation.x,
				rot_y = rotation.y,
				rot_z = rotation.z,
				rot_w = rotation.w
			};
		}

		internal static FTTrackingPose GetConvertedPose(this Transform transform)
		{
			return transform.localToWorldMatrix.GetConvertedPose();
		}

		internal static FTTrackingPose GetConvertedPose(Vector3 position, Quaternion rotation)
		{
			Matrix4x4 matrix = Matrix4x4.TRS(position, rotation, Vector3.one);
			return matrix.GetConvertedPose();
		}
	}
}
