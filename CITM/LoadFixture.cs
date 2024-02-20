using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Xml.Serialization;

using Microsoft.DirectX;

using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.Gui.AspectViewer.Editors;
using Demo3D.Visuals;
using Demo3D.Visuals.Renderers.Mesh;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;

namespace Demo3D.Components
{
    using Properties;

    [Category(nameof(Resources.EndEffectors_Category))]
    [Resources(typeof(Resources))]
    [HelpUrl("load_fixture")]
    public class LoadFixture : ExportableVisualAspect
    {
        public enum StickModes
        {
            Reparenting,
            FixedJoint
        }

        public enum FilterModes
        {
            None,
            Allowed,
            Disallowed
        }

        [Flags]
        public enum Recentering
        {
            None = 0,
            Position = 1 << 0,
            Rotation = 1 << 1,
            Both = Position | Rotation
        }

        private const string GripperBodyVisualName = "GripperBody";

        private StickModes stickMode = StickModes.Reparenting;
        private FilterModes filterMode = FilterModes.None;
        private double radius = 0.05;
        private string filterTypes = "";
        
        private Recentering recenter = Recentering.Both;
        private Vector3 rotationOffset = Vector3.Zero;
        private Vector3 positionOffset = Vector3.Zero;

        private SpherePhysicsAspect geometry = null;
        private CollisionSensorAspect hookedSensor = null;
        private Visual gripperBodyVisual = null;

        private readonly VisualList grippedVisuals = new VisualList();

        [AspectProperty]
        [DefaultValue(StickModes.Reparenting)]
        public StickModes StickMode
        {
            get { return stickMode; }
            set
            {
                if (stickMode != value)
                {
                    ReleaseAll();
                    stickMode = value;
                    GripAll();
                    RaisePropertyChanged(nameof(StickMode));
                }
            }
        }

        [AspectProperty]
        [DefaultValue(FilterModes.None)]
        public FilterModes FilterMode
        {
            get { return filterMode; }
            set
            {
                if (SetProperty(ref filterMode, value))
                {
                    RaisePropertyChanged(nameof(Filtering));
                    RaisePropertyChanged(nameof(FilterTypes));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool Filtering
        {
            get { return filterMode != FilterModes.None; }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(Filtering), IsEnabledPropertyLink = nameof(Filtering))]
        public string FilterTypes
        {
            get { return filterTypes; }
            set { SetProperty(ref filterTypes, value); }
        }

        [AspectProperty]
        [DefaultValue(Recentering.None)]
        public Recentering Recenter
        {
            get { return recenter; }
            set
            {
                if (SetProperty(ref recenter, value))
                {
                    RaisePropertyChanged(nameof(PositionOffset));
                    RaisePropertyChanged(nameof(RotationOffset));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool RecenterPosition
        {
            get { return (recenter & Recentering.Position) != 0; }
        }

        [AspectProperty(IsVisible = false)]
        public bool RecenterRotation
        {
            get { return (recenter & Recentering.Rotation) != 0; }
        }

        [AspectProperty]
        [Distance]
        [AspectEditor(IsVisiblePropertyLink = nameof(RecenterPosition), IsEnabledPropertyLink = nameof(RecenterPosition))]
        public Vector3 PositionOffset
        {
            get { return positionOffset; }
            set { SetProperty(ref positionOffset, value); }
        }

        [AspectProperty]
        [Angle]
        [AspectEditor(IsVisiblePropertyLink = nameof(RecenterRotation), IsEnabledPropertyLink = nameof(RecenterRotation))]
        public Vector3 RotationOffset
        {
            get { return rotationOffset; }
            set { SetProperty(ref rotationOffset, value); }
        }

        [AspectProperty]
        [Distance]
        [DefaultValue(0.05)]
        public double AttachmentRadius
        {
            get { return radius; }
            set
            {
                if (SetProperty(ref radius, value))
                {
                    SetSensorRadius(value);
                }
            }
        }

        [AspectProperty(IsReadOnly = true)]
        public VisualList GrippedVisuals
        {
            get { return grippedVisuals; }
        }

        protected override void OnAdded()
        {
            base.OnAdded();

            // Remove any physics geometry not managed by this aspect.
            var geometryAspects = Visual.FindAspects<PhysicsGeometryAspect>().ToArray();
            foreach (var geometryAspect in geometryAspects)
            {
                if (geometryAspect.AspectManagedBy != this)
                {
                    Visual.RemoveAspect(geometryAspect);
                }
            }

            // Ensure that the visual has physics geometry.
            geometry = Visual.FindAspect<SpherePhysicsAspect>();
            if (geometry == null)
            {
                geometry = Visual.FindCreateAspect<SpherePhysicsAspect>();
                geometry.UseAncestorBody = false;
                geometry.AspectManagedBy = this;
            }

            // Set the sphere geometry radius.
            SetSensorRadius(radius);

            // Add the gripper body visual.
            FindCreateGripperBodyVisual();

            // Find/create and hook sensor.
            HookSensor();
        }

        protected override void OnRemoved()
        {
            RemoveGripperBodyVisual();
            UnhookSensor();

            document.PhysicsEngine.PhysicsStepStarted -= OnPhysicsStepStarted;

            base.OnRemoved();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            grippedVisuals.Clear();

            document.PhysicsEngine.PhysicsStepStarted -= OnPhysicsStepStarted;
            document.PhysicsEngine.PhysicsStepStarted += OnPhysicsStepStarted;

            ToggleMeshRenderers(false);
        }

        protected override void OnReset()
        {
            base.OnReset();

            grippedVisuals.Clear();

            document.PhysicsEngine.PhysicsStepStarted -= OnPhysicsStepStarted;

            ToggleMeshRenderers(true);
        }

        private void FindCreateGripperBodyVisual()
        {
            RemoveGripperBodyVisual();

            gripperBodyVisual = Visual.FindImmediateChild(GripperBodyVisualName);
            if (gripperBodyVisual == null)
            {
                gripperBodyVisual = document.CreateVisual<Visual>();
                gripperBodyVisual.Name = GripperBodyVisualName;
                gripperBodyVisual.Parent = Visual;
                gripperBodyVisual.Location = Vector3.Zero;
            }

            var rigidBodyAspect = gripperBodyVisual.FindAspect<RigidBodyAspect>();
            if (rigidBodyAspect == null)
            {
                rigidBodyAspect = gripperBodyVisual.FindCreateAspect<RigidBodyAspect>();
                rigidBodyAspect.AspectManagedBy = this;
            }

            rigidBodyAspect.Kinematic = true;
        }

        private void RemoveGripperBodyVisual()
        {
            if (gripperBodyVisual != null)
            {
                ReleaseAll();
                gripperBodyVisual.Delete();
                gripperBodyVisual = null;
            }
        }

        private void ToggleMeshRenderers(bool enable)
        {
            var renderers = Visual.FindVisualAndDescendantsAspects<MeshRendererAspect>();
            foreach (var renderer in renderers)
            {
                renderer.IsEnabled = enable;
            }
        }

        private CollisionSensorAspect FindCreateSensor()
        {
            var sensor = Visual.FindAspect<CollisionSensorAspect>();
            if (sensor == null)
            {
                sensor = Visual.FindCreateAspect<CollisionSensorAspect>();
                if (sensor != null)
                {
                    sensor.ControlMode = CollisionSensorControlMode.None;
                    sensor.SenseMultipleLoads = true;
                    sensor.AspectManagedBy = this;
                }
            }

            return sensor;
        }

        private void HookSensor()
        {
            HookSensor(FindCreateSensor());
        }

        private void HookSensor(CollisionSensorAspect sensor)
        {
            if (sensor != null)
            {
                UnhookSensor(); // Unhook any existing sensor.

                hookedSensor = sensor;

                sensor.OnBlocked -= OnSensorBlocked;
                sensor.OnBlocked += OnSensorBlocked;
            }
        }

        private void UnhookSensor()
        {
            if (hookedSensor != null)
            {
                hookedSensor.OnBlocked -= OnSensorBlocked;
                hookedSensor = null;
            }
        }

        private void OnSensorBlocked(Visual blockedVisual)
        {
            // Check for a Fixed Joint to identify whether a gripper is hading off to us in
            // FixedJoint mode or in Reparent mode. If a fixed joint is found, assume we are being
            // haded off to from a gripper in FixedJoint mode. Otherwise, assume we are being haded
            // off to by a gripper in Reparent mode (or any other process).
            var fixedJoint = blockedVisual.FindAspect<DynamicFixedJointAspect>();
            if (fixedJoint != null)
            {
                var gripperVisual = fixedJoint.OtherVisual?.Visual;
                if (gripperVisual.Name == GripperBodyVisualName)
                {
                    fixedJoint.IsEnabledChanged += OnFixedJointRemovedFromLoad;
                }
            }
            else
            {
                Grip(blockedVisual);
            }
        }

        private void OnFixedJointRemovedFromLoad(object sender, EventArgs e)
        {
            if (sender is DynamicFixedJointAspect fixedJointAspect)
            {
                Grip(fixedJointAspect.Visual);
            }
        }

        private void GripAll()
        {
            if (hookedSensor != null)
            {
                var blockingVisuals = hookedSensor.BlockingVisuals.Visuals.ToList();
                foreach (var blockingVisual in blockingVisuals)
                {
                    Grip(blockingVisual);
                }
            }
        }

        private bool Grip(Visual visual)
        {
            if (visual == null || CanGrip(visual) == false || visual.Parent != document.Scene || visual.RigidBody == null || visual.RigidBody.Kinematic) { return false; }

            if (grippedVisuals.Contains(visual) == false)
            {
                RecenterLoad(visual);

                if (visual is PhysicsObject physicsObject)
                {
                    if (physicsObject.BodyType == PhysicsBodyType.Load)
                    {
                        if (physicsObject.Kinematic == false)
                        {
                            if (stickMode == StickModes.FixedJoint)
                            {
                                app.LogMessage("Warning", "Core visuals cannot be gripped/released using fixed joints. Falling back to using reparenting.", Visual);
                            }

                            physicsObject.Kinematic = true;
                            physicsObject.Reparent(gripperBodyVisual);

                            grippedVisuals.Add(physicsObject);
                            RaisePropertyChanged(nameof(GrippedVisuals));

                            // Subsribe to the gripped visual's "parent changed" and "aspect added"
                            // events. This will allow us to  determine when something takes the
                            // load from us.
                            visual.OnParentUpdated.NativeListeners -= OnLoadParentUpdated;
                            visual.OnParentUpdated.NativeListeners += OnLoadParentUpdated;
                            visual.AspectAdded -= OnAspectAddedToLoad;
                            visual.AspectAdded += OnAspectAddedToLoad;
                            return true;
                        }
                    }
                }
                else
                {
                    var rigidBody = visual.FindAspect<RigidBodyAspect>();
                    if (rigidBody != null && rigidBody.Kinematic == false)
                    {
                        switch (stickMode)
                        {
                            case StickModes.Reparenting:
                                rigidBody.Kinematic = true;
                                visual.Reparent(gripperBodyVisual);
                                break;
                            case StickModes.FixedJoint:
                                {
                                    var fixedJoint = new DynamicFixedJointAspect();
                                    fixedJoint.OtherVisual = gripperBodyVisual;
                                    visual.AddAspect(fixedJoint);
                                }
                                break;
                        }

                        grippedVisuals.Add(visual);
                        RaisePropertyChanged(nameof(GrippedVisuals));

                        // Subsribe to the gripped visual's "parent changed" and "aspect added"
                        // events. This will allow us to  determine when something takes the
                        // load from us.
                        visual.OnParentUpdated.NativeListeners -= OnLoadParentUpdated;
                        visual.OnParentUpdated.NativeListeners += OnLoadParentUpdated;
                        visual.AspectAdded -= OnAspectAddedToLoad;
                        visual.AspectAdded += OnAspectAddedToLoad;
                        return true;
                    }
                }
            }

            return false;
        }

        private void OnLoadParentUpdated(Visual sender, Visual oldParent, Visual newParent)
        {
            if (newParent != gripperBodyVisual)
            {
                // Something has taken the load from us by reparenting.
                Release(sender, false);
            }
        }

        private void OnAspectAddedToLoad(Visual sender, object aspect)
        {
            // Something may have taken the load from us by fixed joint.
            if (aspect is DynamicFixedJointAspect)
            {
                var rigidBody = sender.FindAspect<RigidBodyAspect>();
                if (rigidBody != null)
                {
                    // Unparent the load if it is still parented to the gripper body.
                    if (sender.Parent == gripperBodyVisual)
                    {
                        sender.Reparent(sender.Document.Scene);
                    }

                    rigidBody.IsEnabled = true;
                }

                Release(sender);
            }
        }

        private void OnPhysicsStepStarted()
        {
            // Try to grip any loads that are blocking the sensor but not yet gripped.
            // This is necessary because the loads may become "gripable" whilst they
            // are blocking the sensor.
            GripAll();
        }

        private void ReleaseAll()
        {
            for (int i = GrippedVisuals.Count - 1; i >= 0; --i)
            {
                var visualReference = grippedVisuals[i];
                if (visualReference != null)
                {
                    Release(visualReference.Visual);
                }
            }
        }

        private void Release(Visual visual, bool setDynamic = true)
        {
            if (visual != null)
            {
                if (grippedVisuals.Remove(visual))
                {
                    RaisePropertyChanged(nameof(GrippedVisuals));

                    if (visual is PhysicsObject physicsObject)
                    {
                        if (stickMode == StickModes.FixedJoint)
                        {
                            app.LogMessage("Warning", "Core visuals cannot be gripped/released using fixed joints. Falling back to using reparenting.", Visual);
                        }

                        if (setDynamic)
                        {
                            physicsObject.Kinematic = false;
                        }
                    }
                    else
                    {
                        var rigidBody = visual.FindAspect<RigidBodyAspect>();
                        if (rigidBody != null)
                        {
                            switch (stickMode)
                            {
                                case StickModes.Reparenting:
                                    if (setDynamic)
                                    {
                                        rigidBody.Kinematic = false;
                                    }
                                    break;
                                case StickModes.FixedJoint:
                                    foreach (var fixedJoint in visual.FindAspects<DynamicFixedJointAspect>().ToArray())
                                    {
                                        if (fixedJoint.OtherVisual == gripperBodyVisual)
                                        {
                                            visual.RemoveAspect(fixedJoint);
                                            visual.WakeupPhysics();
                                        }
                                    }
                                    break;
                            }
                        }
                    }

                    visual.OnParentUpdated.NativeListeners -= OnLoadParentUpdated;
                    visual.AspectAdded -= OnAspectAddedToLoad;
                }
            }
        }

        private void RecenterLoad(Visual visual)
        {
            if (RecenterPosition)
            {
                visual.WorldLocation = Visual.TransformToWorld(positionOffset);
            }

            if (RecenterRotation)
            {
                visual.WorldRotationDegrees = Visual.TransformToWorldRotationDegrees(rotationOffset);
            }
        }

        private bool CanGrip(Visual visual)
        {
            if (visual != null)
            {
                var types = filterTypes?.Split(',').Select(l => l.Trim()).ToArray() ?? null;
                var visualType = visual.Type;
                switch (filterMode)
                {
                    case FilterModes.Allowed:
                        return types != null && types.Contains(visualType);
                    case FilterModes.Disallowed:
                        return types == null || types.Contains(visualType) == false;
                }

                return true;
            }

            return false;
        }

        private void SetSensorRadius(double value)
        {
            if (geometry != null)
            {
                geometry.Radius = Math.Max(0.01, value);
            }
        }
    }
}
