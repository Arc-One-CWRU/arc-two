using System.Collections;
using System.ComponentModel;

using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.EventQueue;
using Demo3D.Native;
using Demo3D.Visuals;

namespace Demo3D.Components {

    [Resources(typeof(Properties.Resources))]
    [HelpUrl("timerloadsource")]
    public class TimerLoadSource : LoadSource {
        [Time, DefaultValue(2.0)]
        public double ReleaseInterval { get; set; } = 2.0;

        [Time, DefaultValue(0.0)]
        public double InitialInterval { get; set; } = 0.0;

        ITask CreateLoadTask;

        protected override void OnInitialize() {
            base.OnInitialize();
            ScheduleCreateLoad(InitialInterval); // Schedule first load creation.
        }

        protected override void OnEnabled() {
            base.OnEnabled();
            ScheduleCreateLoad(InitialInterval);
        }

        protected override void OnDisabled() {
            CancelCreateLoad();
            base.OnDisabled();
        }

        protected override void OnReset() {
            base.OnReset();
            CancelCreateLoad();
        }

        void ScheduleCreateLoad(double delay) {
            if (CreateLoadTask == null) {
                CreateLoadTask = document.Run(delay, CreateLoad);
            }
        }

        void CancelCreateLoad() {
            if (CreateLoadTask != null) {
                CreateLoadTask.Cancel(nameof(OnDisabled));
                CreateLoadTask = null;
            }
        }

        IEnumerable CreateLoad() {
            CreateLoadTask = null;

            var sensor = Visual.FindAspect<CollisionSensorAspect>();

            if (CongestionZone && sensor != null) {
                // Only create a load when the congestion zone is clear
                yield return Wait.UntilTrue(() => sensor.IsBlocked == false || CongestionZone == false, sensor, this);
            }

            if (IsEnabled) {
                // Clone the load creator
                CloneVisual();

                if (CongestionZone && sensor != null) {
                    // Wait until the sensor is blocked (i.e. a Volumetric physics time-step has completed or 0 time in Linear/Planar physics)
                    yield return Wait.UntilTrue(() => sensor.IsBlocked == true || CongestionZone == false, sensor, this);
                }

                // Schedule next load creation
                ScheduleCreateLoad(ReleaseInterval);
            }
        }
    }
}
