using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Demo3D.Visuals;
using Demo3D.Visuals.Raw;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;

namespace Demo3D.Components {
    using Properties;

    [Category(nameof(Resources.Loads_Category))]
    public abstract class LoadSource : ExportableVisualAspect {
        private bool congestionZone = true;
        private OnLoadCreatedScriptReference onLoadCreated;

        [DefaultValue(true)]
        public bool CongestionZone {
            get { return congestionZone; }
            set { SetProperty(ref congestionZone, value); }
        }

        public static double PlaceholderTransparency { get; set; } = 0.9;

        [AspectProperty(IsVisible = false)]
        [Exportable(false)]
        public OnLoadCreatedScriptReference OnLoadCreated {
            get { return InitProperty(ref onLoadCreated); }
            set { onLoadCreated = value; }
        }

        protected override bool CanAdd(ref string reasonForFailure) {
            if (Visual is CoreVisual) {
                reasonForFailure = "load source aspects cannot be added to core visuals";
                return false;
            }
            return true;
        }

        protected override void OnAdded() {
            base.OnAdded();

            // Show load creator as transparent
            Visual.FadeToTransparencyDeep(PlaceholderTransparency, 0);

            // Disable any rigid body aspect.
            // We only use the rigid body aspect as a seed for the created loads.
            var body = Visual.FindAspect<RigidBodyAspect>();
            if (body != null) {
                body.IsEnabled = false;
            }

            // Ensure load has geometry, create Box if non yet assigned (needed for the congestion zone sensor)
            var geometry = Visual.FindAspect<PhysicsGeometryAspect>();
            if (geometry == null) {
                geometry = Visual.FindCreateAspect<BoxPhysicsAspect>();
                geometry.AspectManagedBy = this;
            }

            // Use load shape as CongestionZone sensor
            var sensor = Visual.FindAspect<CollisionSensorAspect>();
            if (sensor == null) {
                sensor = Visual.FindCreateAspect<CollisionSensorAspect>();
                if (sensor != null) {
                    sensor.ControlMode = CollisionSensorControlMode.None;
                    sensor.AspectManagedBy = this;
                }
            }
        }

        protected override void OnInitialize() {
            base.OnInitialize();

            // Disable any rigid body aspect.
            // We only use the rigid body aspect as a seed for the created loads.
            var body = Visual.FindAspect<RigidBodyAspect>();
            if (body != null) {
                body.IsEnabled = false;
            }
        }

        protected override void OnRemoved() {
            // Remove the collision sensor aspect if there are now no load source aspects.
            if (Visual.HasAspect<LoadSource>() == false) {
                Visual.RemoveAspect<CollisionSensorAspect>();
            }

            base.OnRemoved();
        }

        private void NotifyLoadCreated(Visual load) {
            document.RunScriptNow(onLoadCreated, Visual, load);
        }

        protected Visual CloneVisual() {
            // Clone the load creator as a load
            var clone = Visual.Clone();

            // Remove the CAD import aspects.
            foreach (var visual in clone.VisualAndDescendants) {
                visual.RemoveAspect<CADImport>();
            }

            // Remove all load source aspects
            foreach (var loadSource in new List<LoadSource>(clone.FindAspects<LoadSource>())) {
                clone.RemoveAspect(loadSource);
            }

            // Ensure load has geometry, create Box if non yet assigned
            var geometry = clone.FindAspect<PhysicsGeometryAspect>();
            if (geometry == null) {
                geometry = clone.FindCreateAspect<BoxPhysicsAspect>();
                geometry.AspectManagedBy = this;
            }

            // Create a rigid body for the load (if one doesn't already exist).
            var body = clone.FindCreateAspect<RigidBodyAspect>();
            body.AspectManagedBy = this;
            body.IsEnabled = true;

            // Mark the load as having a Demo3D LoadAspect so it's deleted on reset or hitting the floor
            var load = clone.FindCreateAspect<LoadAspect>();
            load.DeleteOnReset = true;
            load.DeleteWhenFloorHit = true;
            load.AspectManagedBy = this;

            // Remove load source PlaceholderTransparency after creation
            clone.FadeToTransparencyDeep(0.0, 0.2);

            // Ensure that the load is not static.
            clone.IsStatic = false;

            clone.Initialize();
            NotifyLoadCreated(clone);

            return clone;
        }
    }
}
