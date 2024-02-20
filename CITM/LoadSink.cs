using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Microsoft.DirectX;

using Demo3D.Common;
using Demo3D.Visuals;

namespace Demo3D.Components
{
    using Properties;

    [Category(nameof(Resources.Loads_Category))]
    [Resources(typeof(Resources))]
    [HelpUrl("load_sink")]
    public class LoadSink : ExportableVisualAspect
    {
        private CollisionSensorAspect sensor;

        protected override bool CanAdd(ref string reasonForFailure)
        {
            if (Visual is CoreVisual)
            {
                reasonForFailure = "load sink aspects cannot be added to core visuals";
                return false;
            }

            return true;
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            if (IsEnabled)
            {
                HookSensor();
            }
        }

        protected override void OnReset()
        {
            base.OnReset();

            UnhookSensor();
        }

        protected override void OnEnabled()
        {
            HookSensor();

            base.OnEnabled();
        }

        protected override void OnDisabled()
        {
            UnhookSensor();

            base.OnDisabled();
        }

        private CollisionSensorAspect GetSensor()
        {
            // Ensure load has geometry, add a box physics aspect if none is yet assigned.
            var physicsGeometry = Visual.FindAspect<PhysicsGeometryAspect>();
            if (physicsGeometry == null)
            {
                physicsGeometry = Visual.FindCreateAspect<BoxPhysicsAspect>();
                physicsGeometry.AspectManagedBy = this;
            }

            // Use load shape as CongestionZone sensor
            var sensor = Visual.FindAspect<CollisionSensorAspect>();
            if (sensor == null)
            {
                sensor = Visual.FindCreateAspect<CollisionSensorAspect>();
                sensor.SenseMultipleLoads = true;
                sensor.AspectManagedBy = this;
            }

            return sensor;
        }

        private void HookSensor()
        {
            HookSensor(GetSensor());
        }

        private void HookSensor(CollisionSensorAspect sensor)
        {
            UnhookSensor();
            this.sensor = sensor;
            this.sensor.OnBlocked += OnSensorBlocked;

        }

        private void UnhookSensor()
        {
            if (sensor != null)
            {
                sensor.OnBlocked -= OnSensorBlocked;
                sensor = null;
            }
        }

        private void OnSensorBlocked(Visual obj)
        {
            if (obj is PhysicsObject physicsObject)
            {
                if (physicsObject.BodyType == PhysicsBodyType.Load)
                {
                    document.DestroyVisual(obj);
                }
            }
            else if (obj != null)
            {
                var loadAspect = obj.FindAspect<LoadAspect>();
                if (loadAspect != null)
                {
                    document.DestroyVisual(obj);
                }
            }
        }
    }
}
