using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;
using System.Windows.Media;

using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.EventQueue;
using Demo3D.Gui.AspectViewer;
using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.Visuals;
using Demo3D.Visuals.Motor;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motor_Encoders_Category))]
    [HelpUrl("counting_encoder")]
    public sealed class CountingEncoder : ExportableVisualAspect, IEncoder, IBindableItemOwner
    {
        public enum AngleMode
        {
            Discontinuous,
            Continuous
        }

        private MotorAspect motor;
        private double frequency;
        private double countMultiplier;
        private int minCount;
        private int maxCount;
        private int count;

        private Event scheduledEvent;

        private BindableItem<int> countBindableItem;

        [AspectProperty(IsVisible = true), Description("Name of the encoder.")]
        public override string Name
        {
            get => base.Name;
            set => base.Name = value;
        }

        [AspectProperty, Description("The motor that the encoder is currently bound to.")]
        public MotorAspect Motor
        {
            get { return motor; }
        }

        [AspectProperty, Description("The number of times per second the count will be updated.")]
        [Frequency]
        [DefaultValue(10.0)]
        public double Frequency
        {
            get { return frequency; }
            set
            {
                var clampedValue = Math.Min(100.0, Math.Max(0.0, value));
                if (SetProperty(ref frequency, clampedValue))
                {
                    ReschedulePolling();

                    if (clampedValue != value)
                    {
                        RaisePropertyChanged(nameof(Frequency));
                    }
                }
            }
        }

        [AspectProperty(IsVisible = true), Description("The value to multiply the motor travelled value by to calculate the count.")]
        [DefaultValue(1000.0)]
        public double CountMultiplier
        {
            get { return countMultiplier; }
            set
            {
                if (SetProperty(ref countMultiplier, value))
                {
                    RaisePropertyChanged(nameof(CountMultiplier));
                }
            }
        }

        [AspectProperty(IsVisible = true), Description("The minimum value for the count.")]
        [DefaultValue(0)]
        public int MinCount
        {
            get { return minCount; }
            set
            {
                if (SetProperty(ref minCount, value))
                {
                    RaisePropertyChanged(nameof(MinCount));
                }
            }
        }

        [AspectProperty(IsVisible = true), Description("The maximum value for the count.")]
        [DefaultValue(Int32.MaxValue)]
        public int MaxCount
        {
            get { return maxCount; }
            set
            {
                if (SetProperty(ref maxCount, value))
                {
                    RaisePropertyChanged(nameof(MaxCount));
                }
            }
        }

        [AspectProperty(IsVisible = true, IsReadOnly = true), Description("The most recent encoder count.")]
        [DefaultValue(0)]
        public int Count
        {
            get { return count; }
            set
            {
                if (SetProperty(ref count, value))
                {
                    RaisePropertyChanged(nameof(Count));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<int> CountBindableItem
        {
            get { return countBindableItem; }
            private set
            {
                if (countBindableItem != value)
                {
                    countBindableItem?.DetachFromVisual();
                    countBindableItem = value;

                    if (countBindableItem != null)
                    {
                        countBindableItem.Value = Count;
                        countBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                if (CountBindableItem != null) { yield return CountBindableItem; }
            }
        }

        public CountingEncoder()
        {
            motor = null;
            frequency = 10.0;
            countMultiplier = 1000.0;
            minCount = 0;
            maxCount = Int32.MaxValue;
            count = 0;

            countBindableItem = null;
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

            if (motor != null)
            {
                motor.Encoder = null; // Detach the encoder from the motor.
            }

            base.OnRemoved();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            ReschedulePolling();
        }

        protected override void OnReset()
        {
            base.OnReset();

            CancelPolling();
        }

        public bool Attach(MotorAspect motor)
        {
            // If we are already attached to a motor, then the attachment should fail.
            if (this.motor == null && motor != null)
            {
                this.motor = motor;
                if (this.motor != null)
                {
                    // Attachment succeeded!
                    Poll(); // Poll now.
                    ReschedulePolling(); // Schedule polling.
                    Subscribe(); // Subscribe for events on the motor.
                    RaisePropertyChanged(nameof(Count));
                }

                RaisePropertyChanged(nameof(Motor));
                return true;
            }

            return false;
        }

        public void Detach()
        {
            if (motor != null)
            {
                CancelPolling();
                Unsubscribe();
                motor = null;
                count = 0;

                RaisePropertyChanged(nameof(Motor));
                RaisePropertyChanged(nameof(Count));
            }
        }

        public void Pause()
        {
            CancelPolling(); // No point in polling whilst the motor is paused.
        }

        public void Resume(double pauseDuration)
        {
            PollAndSchedule(); // Resume polling once the motor is unpaused.
        }

        private void CreateBindings()
        {
            Debug.Assert(Visual != null);

            CountBindableItem = new WriteToServer<int>(Visual, BindingName(nameof(Count)));

            UpdateBindings();
        }

        private void UpdateBindings()
        {
            if (CountBindableItem != null) { CountBindableItem.IsBindingInterface = TriStateYNM.Yes; }

            UpdateBindingAPI();
        }

        private void RemoveBindings()
        {
            CountBindableItem = null;

            ReleaseBindingName(nameof(Count));

            UpdateBindingAPI();
        }

        private void Subscribe()
        {
            motor.OnAfterReset += OnAfterReset;
        }

        private void Unsubscribe()
        {
            motor.OnAfterReset -= OnAfterReset;
        }

        private void OnAfterReset(MotorAspect motor)
        {
            Poll(); // Initial state may have been applied.
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
            if (motor != null)
            {
                var counts = motor.DistanceTravelled * CountMultiplier;
                var modulus = (double)MaxCount - (double)MinCount + 1.0;
                Count = (int)(Modulo(counts, modulus) + MinCount);
            }
        }

        public int GetCount()
        {
            Poll();
            return Count;
        }

        private static double Modulo(double value, double modulus)
        {
            // modulo function which also handles negative numbers
            double result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}