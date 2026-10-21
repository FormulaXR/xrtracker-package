using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// One viewpoint on a body, described in the body's own coordinates.
	/// </summary>
	/// <remarks>
	/// <para>Two viewpoints are not related by a single angle. Two cameras on the
	/// same bearing at a threefold range see entirely different silhouettes; roll
	/// turns every edge in the image without moving the camera at all; and placing
	/// the object away from the optical axis changes foreshortening across it. So
	/// a viewpoint is stored whole — direction, distance, camera orientation and
	/// off-axis placement — and the four relations between two viewpoints are
	/// measured from those rather than stored.</para>
	/// <para>Every quantity here is geometry. Nothing in this type reads the
	/// image, a residual, a quality value, a pose error, or a clock, which is what
	/// makes an admission or retrieval decision taken from it reproducible from
	/// the poses alone.</para>
	/// </remarks>
	internal readonly struct RecoveryViewDescriptor
	{
		/// <summary>Below this the camera sits on the body origin and no direction exists.</summary>
		private const float MINIMUM_DISTANCE_METERS = 1e-6f;

		private RecoveryViewDescriptor(Vector3 directionInBody, float distanceMeters,
			Quaternion bodyFromCameraRotation, float offAxisDegrees)
		{
			DirectionInBody = directionInBody;
			DistanceMeters = distanceMeters;
			BodyFromCameraRotation = bodyFromCameraRotation;
			OffAxisDegrees = offAxisDegrees;
			IsValid = true;
		}

		/// <summary>Whether this descriptor describes a viewpoint at all.</summary>
		public bool IsValid { get; }

		/// <summary>Unit direction from the body origin to the camera, in body coordinates.</summary>
		public Vector3 DirectionInBody { get; }

		/// <summary>Distance from the body origin to the camera, in metres.</summary>
		public float DistanceMeters { get; }

		/// <summary>Rotation carrying camera axes onto body axes.</summary>
		public Quaternion BodyFromCameraRotation { get; }

		/// <summary>Angle between the optical axis and the direction to the body, in degrees.</summary>
		public float OffAxisDegrees { get; }

		/// <summary>
		/// Describes the viewpoint a <c>camera_from_body</c> pose stands at.
		/// </summary>
		/// <param name="cameraFromBody">
		/// The pose carrying body coordinates into camera coordinates, in metres.
		/// </param>
		/// <returns>False when the pose is unusable, leaving the descriptor invalid.</returns>
		public static bool TryDescribe(Matrix4x4 cameraFromBody, out RecoveryViewDescriptor descriptor)
		{
			descriptor = default;
			if (!IsFinite(cameraFromBody))
				return false;

			Matrix4x4 bodyFromCamera = cameraFromBody.inverse;
			if (!IsFinite(bodyFromCamera))
				return false;

			Vector3 cameraInBody = bodyFromCamera.GetPosition();
			float distance = cameraInBody.magnitude;
			if (!(distance > MINIMUM_DISTANCE_METERS) || float.IsInfinity(distance))
				return false;

			Quaternion bodyFromCameraRotation = bodyFromCamera.rotation;
			if (!IsFinite(bodyFromCameraRotation))
				return false;

			// The body origin sits at the pose's translation in camera coordinates,
			// and a Unity camera looks along its own +z, so the angle between the two
			// is the off-axis placement.
			Vector3 bodyInCamera = cameraFromBody.GetPosition();
			float bodyDistance = bodyInCamera.magnitude;
			float offAxis = bodyDistance > MINIMUM_DISTANCE_METERS
				? Degrees(Mathf.Clamp(bodyInCamera.z / bodyDistance, -1f, 1f))
				: 0f;

			descriptor = new RecoveryViewDescriptor(
				cameraInBody / distance, distance, bodyFromCameraRotation, offAxis);
			return true;
		}

		/// <summary>
		/// Angle between two viewing directions about the body centre, in degrees.
		/// </summary>
		/// <remarks>
		/// Taken from the cross and dot products rather than from the inverse
		/// cosine of the dot alone. Near zero the dot product's rounding is
		/// amplified into a fifth of a degree of angle by the inverse cosine, and
		/// the spacing rule asks about small angles.
		/// </remarks>
		public static float BearingDegrees(RecoveryViewDescriptor a, RecoveryViewDescriptor b)
		{
			if (!a.IsValid || !b.IsValid)
				return float.NaN;
			Vector3 cross = Vector3.Cross(a.DirectionInBody, b.DirectionInBody);
			return Mathf.Atan2(cross.magnitude, Vector3.Dot(a.DirectionInBody, b.DirectionInBody))
				* Mathf.Rad2Deg;
		}

		/// <summary>Query distance divided by stored distance.</summary>
		public static float RangeRatio(RecoveryViewDescriptor query, RecoveryViewDescriptor stored)
		{
			if (!query.IsValid || !stored.IsValid || !(stored.DistanceMeters > 0f))
				return float.NaN;
			return query.DistanceMeters / stored.DistanceMeters;
		}

		/// <summary>
		/// Rotation about the optical axis between two viewpoints, in degrees.
		/// </summary>
		/// <remarks>
		/// Only separable from bearing when the two share a viewing direction. It
		/// is one of four ordered keys rather than a quantity read on its own.
		/// </remarks>
		public static float RollDegrees(RecoveryViewDescriptor query, RecoveryViewDescriptor stored)
		{
			if (!query.IsValid || !stored.IsValid)
				return float.NaN;
			Quaternion relative = Quaternion.Inverse(query.BodyFromCameraRotation)
				* stored.BodyFromCameraRotation;
			Matrix4x4 matrix = Matrix4x4.Rotate(relative);
			return Mathf.Atan2(matrix[1, 0], matrix[0, 0]) * Mathf.Rad2Deg;
		}

		/// <summary>Change in off-axis placement between two viewpoints, in degrees.</summary>
		public static float OffAxisDeltaDegrees(RecoveryViewDescriptor query,
			RecoveryViewDescriptor stored)
		{
			if (!query.IsValid || !stored.IsValid)
				return float.NaN;
			return query.OffAxisDegrees - stored.OffAxisDegrees;
		}

		private static float Degrees(float cosine) => Mathf.Acos(cosine) * Mathf.Rad2Deg;

		private static bool IsFinite(Matrix4x4 matrix)
		{
			for (int row = 0; row < 4; ++row)
			{
				for (int column = 0; column < 4; ++column)
				{
					float value = matrix[row, column];
					if (float.IsNaN(value) || float.IsInfinity(value))
						return false;
				}
			}
			return true;
		}

		private static bool IsFinite(Quaternion rotation)
		{
			return !float.IsNaN(rotation.x) && !float.IsInfinity(rotation.x) &&
				!float.IsNaN(rotation.y) && !float.IsInfinity(rotation.y) &&
				!float.IsNaN(rotation.z) && !float.IsInfinity(rotation.z) &&
				!float.IsNaN(rotation.w) && !float.IsInfinity(rotation.w) &&
				Quaternion.Dot(rotation, rotation) > 0f;
		}
	}
}
