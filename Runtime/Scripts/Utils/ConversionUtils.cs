using UnityEngine;

namespace IV.FormulaTracker
{
	internal static class ConversionUtils
	{
		private static readonly Matrix4x4 InvertedY = Matrix4x4.Scale(new Vector3(1, -1, 1));

		internal static Matrix4x4 ConvertMatrix(Matrix4x4 input)
		{
			return InvertedY * input * InvertedY;
		}

		/// <summary>
		/// Whether a position and rotation pair can be used at all.
		/// </summary>
		/// <remarks>
		/// Both tests are exact and need no calibrated bound: a component either is
		/// a number or is not, and a rotation of zero length names no orientation.
		/// A near-unit rotation whose length is off by rounding is deliberately
		/// accepted — deciding how far from unit is too far needs a bound this layer
		/// has no authority to choose.
		/// </remarks>
		public static bool IsUsablePose(Vector3 position, Quaternion rotation)
		{
			if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z))
				return false;
			if (float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
				return false;
			if (float.IsNaN(rotation.x) || float.IsNaN(rotation.y) ||
				float.IsNaN(rotation.z) || float.IsNaN(rotation.w))
				return false;
			if (float.IsInfinity(rotation.x) || float.IsInfinity(rotation.y) ||
				float.IsInfinity(rotation.z) || float.IsInfinity(rotation.w))
				return false;
			return Quaternion.Dot(rotation, rotation) > 0f;
		}

		/// <summary>
		/// Converts a native pose and reports whether the result can be used.
		/// </summary>
		/// <remarks>
		/// The unchecked <see cref="GetTransformation"/> stays for callers that have
		/// already validated; every path that takes a pose from the tracker and
		/// stores, displays or writes it back goes through this one, because a
		/// non-finite value reaching a <c>Transform</c> is not recoverable by the
		/// engine and a stored one survives until a reset. The raw numbers are judged
		/// before the conversion, because <c>Matrix4x4.TRS</c> logs an error for a
		/// rotation of zero length and the conversion of a rotation that is not a
		/// number is not defined, so a judgement made only on what the conversion
		/// returned would come too late.
		/// </remarks>
		internal static bool TryGetTransformation(this FTTrackingPose pose, out Vector3 position, out Quaternion rotation)
		{
			if (!IsUsablePose(new Vector3(pose.pos_x, pose.pos_y, pose.pos_z), new Quaternion(pose.rot_x, pose.rot_y, pose.rot_z, pose.rot_w)))
			{
				position = Vector3.zero;
				rotation = Quaternion.identity;
				return false;
			}

			pose.GetTransformation(out position, out rotation);
			if (IsUsablePose(position, rotation))
				return true;

			position = Vector3.zero;
			rotation = Quaternion.identity;
			return false;
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
