using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;

using Microsoft.DirectX;

using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.EventQueue;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;
using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.Visuals;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Sensors_Category))]
    [HelpUrl("motion_sensor")]
    public sealed class MotionSensor : ExportableVisualAspect, IBindableItemOwner
    {
        [Flags]
        public enum Output
        {
            None = 0,
            Position = 1 << 0,
            Velocity = 1 << 1,
            Acceleration = 1 << 2
        }

        public struct DataPoint
        {
            public Vector3 Position;
            public double Time;

            public DataPoint(Vector3 position, double time)
            {
                Position = position;
                Time = time;
            }
        }

        private VisualPoint anchor;
        private VisualNormal axis;
        private double frequency;
        private Output outputs;

        private double position;
        private double displacement;
        private double velocity;
        private double acceleration;

        private Vector3 initialPosition;

        private int count;
        private DataPoint[] points;

        private Event scheduledEvent;

        private BindableItem<double> positionBindableItem;
        private BindableItem<double> velocityBindableItem;
        private BindableItem<double> accelerationBindableItem;

        [AspectProperty]
        [Required]
        public VisualPoint Anchor
        {
            get { return anchor; }
            set { SetProperty(ref anchor, value); }
        }

        [AspectProperty]
        public VisualNormal Axis
        {
            get { return axis; }
            set { SetProperty(ref axis, value); }
        }

        [AspectProperty]
        [DefaultValue(10.0)]
        [Frequency]
        public double Frequency
        {
            get { return frequency; }
            set
            {
                if (SetProperty(ref frequency, Math.Min(100.0, Math.Max(0.0, value))))
                {
                    if (IsInitialized)
                    {
                        ReschedulePolling();
                    }
                }
            }
        }

        [AspectProperty]
        [DefaultValue(Output.None)]
        public Output Outputs
        {
            get { return outputs; }
            set
            {
                if (SetProperty(ref outputs, value))
                {
                    UpdateBindings();
                }
            }
        }

        [AspectProperty]
        [Distance]
        [XmlIgnore]
        public double Position
        {
            get { return position; }
            private set
            {
                if (SetProperty(ref position, value))
                {
                    if ((outputs & Output.Position) != 0 && PositionBindableItem != null)
                    {
                        PositionBindableItem.Value = position;
                    }
                }
            }
        }

        [AspectProperty]
        [Speed]
        [XmlIgnore]
        public double Velocity
        {
            get { return velocity; }
            private set
            {
                if (SetProperty(ref velocity, value))
                {
                    if ((outputs & Output.Velocity) != 0 && VelocityBindableItem != null)
                    {
                        VelocityBindableItem.Value = velocity;
                    }
                }
            }
        }

        [AspectProperty]
        [Acceleration]
        [XmlIgnore]
        public double Acceleration
        {
            get { return acceleration; }
            private set
            {
                if (SetProperty(ref acceleration, value))
                {
                    if ((outputs & Output.Acceleration) != 0 && AccelerationBindableItem != null)
                    {
                        AccelerationBindableItem.Value = acceleration;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<double> PositionBindableItem
        {
            get { return positionBindableItem; }
            private set
            {
                if (positionBindableItem != value)
                {
                    positionBindableItem?.DetachFromVisual();
                    positionBindableItem = value;

                    if (positionBindableItem != null)
                    {
                        positionBindableItem.Value = Position;
                        positionBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<double> VelocityBindableItem
        {
            get { return velocityBindableItem; }
            private set
            {
                if (velocityBindableItem != value)
                {
                    velocityBindableItem?.DetachFromVisual();
                    velocityBindableItem = value;

                    if (velocityBindableItem != null)
                    {
                        velocityBindableItem.Value = Velocity;
                        velocityBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<double> AccelerationBindableItem
        {
            get { return accelerationBindableItem; }
            private set
            {
                if (accelerationBindableItem != value)
                {
                    accelerationBindableItem?.DetachFromVisual();
                    accelerationBindableItem = value;

                    if (accelerationBindableItem != null)
                    {
                        accelerationBindableItem.Value = Velocity;
                        accelerationBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                if (PositionBindableItem != null) { yield return PositionBindableItem; }
                if (VelocityBindableItem != null) { yield return VelocityBindableItem; }
                if (AccelerationBindableItem != null) { yield return AccelerationBindableItem; }
            }
        }

        public MotionSensor()
        {
            anchor = null;
            axis = null;
            frequency = 10.0;
            outputs = Output.None;
            position = 0.0;
            velocity = 0.0;
            acceleration = 0.0;

            initialPosition = Vector3.Zero;

            count = 0;
            points = new DataPoint[3];

            scheduledEvent = null;
            positionBindableItem = null;
            velocityBindableItem = null;
            accelerationBindableItem = null;
        }

        protected override void OnAssigned()
        {
            base.OnAssigned();

            CleanupBindingAPI();

            if (Visual != null)
            {
                CreateBindings();
            }
            else
            {
                RemoveBindings();
            }
        }

        protected override void OnRemoved()
        {
            CancelPolling();

            base.OnRemoved();
        }

        protected override void OnDisabled()
        {
            CancelPolling();
            count = 0;
            base.OnDisabled();
        }

        protected override void OnEnabled()
        {
            base.OnEnabled();
            count = 0;
            if (IsInitialized)
            {
                ReschedulePolling();
            }
        }

        protected override void OnReset()
        {
            base.OnReset();
            count = 0;
            CancelPolling();
            Poll();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            count = 0;
            SetInitialPosition();
            ReschedulePolling();
        }

        private void CreateBindings()
        {
            PositionBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(Position)));
            VelocityBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(Velocity)));
            AccelerationBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(Acceleration)));

            UpdateBindings();
        }

        private void UpdateBindings()
        {
            if (PositionBindableItem != null) { PositionBindableItem.IsBindingInterface = Outputs.HasFlag(Output.Position) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (VelocityBindableItem != null) { VelocityBindableItem.IsBindingInterface = Outputs.HasFlag(Output.Velocity) ? TriStateYNM.Yes : TriStateYNM.No; }
            if (AccelerationBindableItem != null) { AccelerationBindableItem.IsBindingInterface = Outputs.HasFlag(Output.Acceleration) ? TriStateYNM.Yes : TriStateYNM.No; }

            UpdateBindingAPI();
        }

        private void RemoveBindings()
        {
            PositionBindableItem = null;
            VelocityBindableItem = null;
            AccelerationBindableItem = null;

            ReleaseBindingName(nameof(Position));
            ReleaseBindingName(nameof(Velocity));
            ReleaseBindingName(nameof(Acceleration));

            UpdateBindingAPI();
        }

        public void SetInitialPosition()
        {
            SetInitialPosition(anchor.WorldLocation);
        }

        public void SetInitialPosition(Vector3 worldPosition)
        {
            initialPosition = worldPosition;
        }

        private void ReschedulePolling()
        {
            if (Visual != null)
            {
                CancelPolling();
                if (frequency > 0.0)
                {
                    PollAndSchedule();
                }
            }
        }

        private void PollAndSchedule()
        {
            Poll();

            double seconds = 1.0 / frequency;
            var secondsAsFixed = Fixed.RoundUp(ref seconds);
            scheduledEvent = Visual.Document.EventQueue.ScheduleAction(secondsAsFixed, PollAndSchedule);
        }

        private void CancelPolling()
        {
            scheduledEvent?.Cancel();
            scheduledEvent = null;
        }

        private void Poll()
        {
            Add(new DataPoint(anchor.WorldLocation, document.Time));

            var t1 = points[0].Time;
            var t2 = points[1].Time;
            var t3 = points[2].Time;

            var p1 = points[0].Position;
            var p2 = points[1].Position;
            var p3 = points[2].Position;

            double d1, d2, v1, v2;
            if (axis != null)
            {
                var normal = axis.InitialWorldNormal;

                d1 = Vector3.Dot(p2 - p1, normal);
                d2 = Vector3.Dot(p3 - p2, normal);
                v1 = d1 / (t2 - t1);
                v2 = d2 / (t3 - t2);
            }
            else
            {
                d1 = Vector3.Length(p2 - p1);
                d2 = Vector3.Length(p3 - p2);
                v1 = d1 / (t2 - t1);
                v2 = d2 / (t3 - t2);
            }

            var a = 2.0 * (v2 - v1) / (t3 - t1);

            switch (count)
            {
                case 1:
                    Position = (axis != null) ? Vector3.Dot(p1 - initialPosition, axis.InitialWorldNormal) : Vector3.Length(p1 - initialPosition);
                    Velocity = 0.0;
                    Acceleration = 0.0;
                    break;
                case 2:
                    Position = (axis != null) ? Vector3.Dot(p2 - initialPosition, axis.InitialWorldNormal) : Vector3.Length(p2 - initialPosition);
                    Velocity = v1;
                    Acceleration = 0.0;
                    break;
                case 3:
                    Position = (axis != null) ? Vector3.Dot(p3 - initialPosition, axis.InitialWorldNormal) : Vector3.Length(p3 - initialPosition); ;
                    Velocity = v2;
                    Acceleration = a;
                    break;
            }
        }

        private void Add(DataPoint point)
        {
            if (count == 0 || points[count - 1].Time < point.Time)
            {
                if (count < 3)
                {
                    points[count] = point;
                    count += 1;
                }
                else
                {
                    points[0] = points[1];
                    points[1] = points[2];
                    points[2] = point;
                }
            }
        }
    }
}
