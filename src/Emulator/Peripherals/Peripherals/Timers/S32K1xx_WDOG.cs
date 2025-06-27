//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.Timers
{
    // All information was taken from the S32K1xx Reference Manual (RM)
    // Chapter 23: Watchdog Timer (WDOG)
    // https://www.nxp.com/webapp/Download?colCode=S32K1XXRM
    public class S32K1xx_WDOG : BasicDoubleWordPeripheral, IKnownSize
    {
        public S32K1xx_WDOG(IMachine machine, uint clockScaleFactor = 1) : base(machine)
        {
            if(clockScaleFactor == 0)
            {
                throw new ConstructionException("Clock scale factor cannot be zero");
            }
            ClockScaleFactor = clockScaleFactor;

            IRQ = new GPIO();

            internalTimer = new LimitTimer(machine.ClockSource, (DefaultClockFrequency / ClockScaleFactor), this, "WDOG", direction: Time.Direction.Ascending, eventEnabled: true);
            internalTimer.Limit = DefaultWatchdogTimeout / ClockScaleFactor;
            internalTimer.LimitReached += () => PerformWatchdogReset();

            DefineRegisters();

            Reset();
        }

        public override void Reset()
        {
            this.Log(LogLevel.Debug, "Resetting peripheral");

            base.Reset();

            IRQ.Unset();

            Init();

            // At startup the WDOG is enabled, but unlocked. This special case allows initial configuration
            // of the WDOG (even though allowUpdates is not set) before it is locked. The first write to Control and Status register
            // will lock the WDOG and prevent further configuration.
            isStartupConfig = true;
            internalTimer.Enabled = true;
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            Registers reg = (Registers)offset;

            if(reg != Registers.Counter && !IsConfigureable())
            {
                this.Log(LogLevel.Warning, "Tried to write {0} while WDOG is locked!", reg);
                return;
            }

            base.WriteDoubleWord(offset, value);
            if(reg == Registers.ControlAndStatus && isStartupConfig)
            {
                // After the first write to Control and Status, the watchdog is no longer in startup configuration
                this.Log(LogLevel.Info, "First write to Control and Status register, WDOG is now configured");
                isStartupConfig = false;
                isLocked = true;
            }
        }

        public long Size => 0x1000;

        public GPIO IRQ { get; }

        // To save resources we can scale the WDOG clocking down by a factor
        public uint ClockScaleFactor = 1;

        private void Init()
        {
            internalTimer.Enabled = false;
            internalTimer.Value = 0;
            internalTimer.Limit = DefaultWatchdogTimeout / ClockScaleFactor;
            internalTimer.Divider = isPrescalerEnabled.Value ? Prescaler : 1;

            previousSequenceKey = 0;
            sequenceState = SequenceStates.None;

            isPendingReset = false;
            isLocked = false;
            isStartupConfig = false;
            isReconfigSuccess = false;
        }

        private bool IsConfigureable()
        {
            // If the watchdog is not enabled, it can be configured always
            if(!isEnabled.Value)
            {
                return true;
            }

            // If its enabled, it can only be configured if it is unlocked and updates are allowed
            return !isLocked && (allowUpdates.Value || isStartupConfig);
        }

        private void HandleSequences(ushort key)
        {
            Tuple<ushort, ushort> keyGiven = new Tuple<ushort, ushort>(previousSequenceKey, key);

            // First we need to check if the value is part of any sequence key(s) at all
            if(key != refreshSequenceKey.Item1 && key != refreshSequenceKey.Item2
               && key != unlockSequenceKey.Item1 && key != unlockSequenceKey.Item2)
            {
                this.Log(LogLevel.Warning, "Received invalid sequence key: {0}", key);
                PerformWatchdogReset("WDOG_INVALID_SEQUENCE_RESET");
                return;
            }

            if(sequenceState == SequenceStates.None)
            {
                sequenceState = SequenceStates.InProgress;
                previousSequenceKey = key;
                return;
            }

            if(!(sequenceState == SequenceStates.InProgress && (keyGiven.Equals(refreshSequenceKey) || keyGiven.Equals(unlockSequenceKey))))
            {
                this.Log(LogLevel.Error, "Invalid sequence received: {0}", keyGiven);
                PerformWatchdogReset("WDOG_INVALID_SEQUENCE_RESET");
                return;
            }

            sequenceState = SequenceStates.Completed;
            this.Log(LogLevel.Noisy, "Valid Sequence received: {0}", keyGiven);

            // We only reach here if the sequence is valid and completed, now we have to handle accordingly
            if(keyGiven.Equals(refreshSequenceKey))
            {
                this.Log(LogLevel.Debug, "Received refresh sequence key: {0}", keyGiven);
                this.Log(LogLevel.Noisy, "Window mode: {0}, Window start value: {1}, Internal timer value: {2}",
                    isWindowMode.Value, windowStartValue.Value, internalTimer.Value);
                if(isWindowMode.Value && windowStartValue.Value > internalTimer.Value)
                {
                    this.Log(LogLevel.Noisy, "Tried to refresh while in window mode and window is closed!");
                    PerformWatchdogReset("WDOG_REFRESH_IN_CLOSED_WINDOW_RESET");
                    return;
                }
                internalTimer.Value = 0;
            }
            else if(keyGiven.Equals(unlockSequenceKey))
            {
                this.Log(LogLevel.Debug, "Received unlock sequence key: {0}", keyGiven);
                isReconfigSuccess = false;
                if(!isLocked)
                {
                    this.Log(LogLevel.Warning, "Tried to unlock while it is already unlocked");
                    return;
                }
                isLocked = false;
                // Trigger automatic relock after 128 ticks of system/bus clock
                machine.ScheduleAction(Time.TimeInterval.FromTicks(InterruptResetDelayTicks), (_) =>
                {
                    if(!isEnabled.Value)
                    {
                        this.Log(LogLevel.Noisy, "No need to relock, WDOG was disabled in the meantime");
                        return;
                    }
                    isLocked = true;
                    this.Log(LogLevel.Info, "Relocked after unlock sequence");
                }, name: "WDOG lock after unlock sequence");
            }
            else
            {
                this.Log(LogLevel.Debug, "Nothing to do for sequence: {0}", keyGiven);
            }

            // Since the sequence was handled, we can reset the state
            previousSequenceKey = 0;
            sequenceState = SequenceStates.None;
        }

        private void PerformWatchdogReset(string reason = "WDOG_TIMEOUT_RESET")
        {
            if(!isEnabled.Value)
            {
                this.Log(LogLevel.Error, "Tried to issue watchdog reset while it is disabled!");
                return;
            }

            if(!isPendingReset && doGenerateInterrupt.Value)
            {
                IRQ.Set(true);
                this.Log(LogLevel.Debug, "Scheduling delayed watchdog reset due to interrupt generation");
                machine.ScheduleAction(Time.TimeInterval.FromTicks(InterruptResetDelayTicks), (_) =>
                {
                    PerformWatchdogReset(reason);
                }
                , name: "Delayed WDOG reset");
            }
            else
            {
                this.Log(LogLevel.Warning, "Performing {0}", reason);
                machine.RequestReset();
            }
            isPendingReset = true;
        }

        private void DefineRegisters()
        {
            Registers.ControlAndStatus.Define(this, 0x00002980)
                .WithReservedBits(16, 16)
                .WithFlag(15, out isWindowMode, name: "WindowMode",
                    changeCallback: (_, value) =>
                    {
                        isWindowMode.Value = value;
                        this.Log(LogLevel.Noisy, $"Window mode set to {value}");
                    })
                .WithFlag(14, FieldMode.Read | FieldMode.WriteOneToClear, name: "InterruptFlag", valueProviderCallback: _ => isPendingReset && doGenerateInterrupt.Value)
                .WithFlag(13, out isCmd32Enabled, name: "CMD32Enable")
                .WithFlag(12, out isPrescalerEnabled, name: "PrescalerEnable")
                .WithFlag(11, FieldMode.Read, name: "Unlocked", valueProviderCallback: _ => !isLocked)
                .WithFlag(10, FieldMode.Read, name: "ReconfigurationSuccess", valueProviderCallback: _ => isReconfigSuccess)
                .WithEnumField(8, 2, out clockSource, name: "ClockSource", changeCallback: (_, value) =>
                {
                    switch(value)
                    {
                    case ClockSources.LPO:
                        internalTimer.Frequency = DefaultClockFrequency / ClockScaleFactor;
                        break;
                    case ClockSources.SIRC:
                        internalTimer.Frequency = 8000000 / ClockScaleFactor; // 8 MHz
                        break;
                    case ClockSources.SOSC:
                        internalTimer.Frequency = 8000000 / ClockScaleFactor; // 8 MHz
                        break;
                    case ClockSources.BUS:
                        // FIXME: This should be set to the actual BUS clock frequency
                        // For now, we assume a default frequency of 24 MHz
                        internalTimer.Frequency = 24000000000 / ClockScaleFactor; // BUS clock frequency
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(value), value, null);
                    }
                })
                .WithFlag(7, out isEnabled, name: "Enable", changeCallback: (_, value) =>
                    {
                        this.Log(LogLevel.Noisy, value ? "Enabling" : "Disabling");
                        Init();

                        internalTimer.Enabled = value;
                        isLocked = value;
                    },
                    writeCallback: (_, value) =>
                    {
                        this.Log(LogLevel.Noisy, $"Reconfiguration success set to {value}");
                        isReconfigSuccess = value;
                    }
                )
                .WithFlag(6, out doGenerateInterrupt, name: "GenerateInterrupt")
                .WithFlag(5, out allowUpdates, name: "AllowUpdates")
                .WithEnumField(3, 2, out testMode, name: "TestMode")
                .WithTaggedFlag("DebugEnable", 2)
                .WithTaggedFlag("WaitEnable", 1)
                .WithTaggedFlag("StopEnable", 0)
                ;

            Registers.Counter.Define(this, 0x00000000)
                .WithValueField(0, 32, name: "Counter",
                    valueProviderCallback: _ => internalTimer.Value,
                    writeCallback: (_, value) =>
                    {
                        ushort upperBits = (ushort)((value >> 16) & 0xFFFF);
                        if(!isCmd32Enabled.Value && upperBits != 0)
                        {
                            this.Log(LogLevel.Warning, "Tried to write upper bits in Counter register while CMD32 is disabled");
                        }
                        else
                        {
                            this.Log(LogLevel.Noisy, $"Executing 32bit command with 0x{upperBits:X}, {value:X}");
                            HandleSequences(upperBits);
                        }
                        // Allow 16 bit writes always
                        HandleSequences((ushort)value);
                    }
                );

            Registers.TimeoutValue.Define(this, (DefaultWatchdogTimeout / ClockScaleFactor))
                .WithReservedBits(16, 16)
                .WithValueField(0, 16, name: "TimeoutValue",
                    valueProviderCallback: _ => (ushort)internalTimer.Limit,
                    writeCallback: (_, value) => internalTimer.Limit = Math.Max(MinimalWatchdogTimeout, value));

            Registers.Window.Define(this)
                .WithReservedBits(16, 16)
                .WithValueField(0, 16, out windowStartValue, name: "WindowStartValue",
                    changeCallback: (_, value) =>
                    {
                        windowStartValue.Value = value;
                        this.Log(LogLevel.Noisy, $"Window start value set to {value}");
                    })
            ;
        }

        private readonly LimitTimer internalTimer;
        private readonly Tuple<ushort, ushort> refreshSequenceKey =
            new Tuple<ushort, ushort>(0xA602, 0xB480);
        private readonly Tuple<ushort, ushort> unlockSequenceKey =
            new Tuple<ushort, ushort>(0xC520, 0xD928);

        // Default LPO clock frequency is 128 kHz
        private const uint DefaultClockFrequency = 128000;

        private const int Prescaler = 256; // Active when prescalerEnable is set

        private const int InterruptResetDelayTicks = 128; // 128 ticks of BUS clock after interrupt generation

        private const int MinimalWatchdogTimeout = 3;

        // 1024 cycles of LPO clock (128 kHz) -> ~8ms
        private const uint DefaultWatchdogTimeout = 1024;

        // Control and Status Register fields
        private IEnumRegisterField<TestModes> testMode;
        private IFlagRegisterField allowUpdates;
        private IFlagRegisterField isPrescalerEnabled;
        private IFlagRegisterField isEnabled;
        private IFlagRegisterField doGenerateInterrupt;
        private IEnumRegisterField<ClockSources> clockSource;
        private IFlagRegisterField isCmd32Enabled;
        private IFlagRegisterField isWindowMode;

        // Other register fields
        private IValueRegisterField windowStartValue;

        private ushort previousSequenceKey;
        private SequenceStates sequenceState;

        private bool isPendingReset;
        private bool isStartupConfig;

        private bool isLocked;
        private bool isReconfigSuccess;

        private enum SequenceStates
        {
            None,
            InProgress,
            Completed
        }

        private enum ClockSources
        {
            LPO = 0, // Low Power Oscillator
            SIRC = 1, // Slow Internal RC Oscillator
            SOSC = 2, // System Oscillator
            BUS = 3 // Bus Clock
        }

        private enum TestModes
        {
            Disabled = 0,
            User = 1,
            TestLowCnt = 2,
            TestHighCnt = 3
        }

        private enum Registers
        {
            ControlAndStatus = 0x0, // CS
            Counter = 0x4, // CNT
            TimeoutValue = 0x8, // TOVAL
            Window = 0xC, // WIN
        }
    }
}
