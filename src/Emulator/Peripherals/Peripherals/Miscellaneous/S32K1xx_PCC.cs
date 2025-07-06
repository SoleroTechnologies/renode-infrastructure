//
// Copyright (c) 2010-2022 Antmicro
// Copyright (c) 2022 ION Mobility
//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public interface IClockGateControl
    {
        bool IsEnabled(string peripheralName);
    }

    // PCC - Peripheral Clock Control
    // This is a stub providing a bare minimum of configuration options along with some reasonable default values.
    // It's not supposed to be understood as a fully-fledged Renode model.
    // Its implementing the IClockGateControl interface, which allows checking if a peripheral is enabled.
    public class S32K1xx_PCC : BasicDoubleWordPeripheral, IKnownSize, IClockGateControl
    {
        public S32K1xx_PCC(IMachine machine, string cpuName) : base(machine)
        {
            pccRegs = new Dictionary<Registers, PCCRegister>();

            this.Log(LogLevel.Debug, "Creating PCC registers for CPU {0}", cpuName);

            foreach(Registers reg in Enum.GetValues(typeof(Registers)))
            {
                bool isPresent = true;
                UInt32 resetValue = 0x80000000;
                this.Log(LogLevel.Debug, "Creating PCC register {0} at offset 0x{1:X}", reg, (ushort)reg);
                if(reg == Registers.FTFC)
                {
                    resetValue = 0xC0000000;
                    if(cpuName.Contains("S32K144W"))
                    {
                        this.Log(LogLevel.Info, "FTFC is named FTMC on S32K144W");
                    }
                }
                else if(reg == Registers.CMU0 || reg == Registers.CMU1)
                {
                    // CMU0 and CMU1 are only available on S32K11x
                    if(!cpuName.Contains("S32K11"))
                    {
                        this.Log(LogLevel.Info, "CMU0 and CMU1 are only available on S32K11x");
                        isPresent = false;
                        resetValue = 0x00000000; // Reset value for CMU0 and CMU1 when not present
                    }
                }

                pccRegs[reg] = new PCCRegister((ushort)reg, isPresent);
                reg.Define(this, resetValue)
                    .WithFlag(31, FieldMode.Read, name: "PR - Present", valueProviderCallback: _ => pccRegs[reg].IsPresent)
                    .WithFlag(30, name: "CGC - Clock Gate Control", valueProviderCallback: _ => pccRegs[reg].IsEnabled, changeCallback: (_, value) => { pccRegs[reg].IsEnabled = value; })
                    .WithReservedBits(0, 30);
            }
        }

        public override void Reset()
        {
            this.Log(LogLevel.Debug, "Resetting peripheral");

            base.Reset();
        }

        public bool IsEnabled(string peripheralName)
        {
            if(!Enum.TryParse(peripheralName, true, out Registers regEnum))
            {
                this.Log(LogLevel.Warning, "Peripheral {0} not found in PCC", peripheralName);
                return false;
            }
            this.Log(LogLevel.Debug, "Checking if peripheral {0} is enabled", peripheralName);
            return pccRegs[regEnum].IsEnabled;
        }

        public long Size => 0x1000;

        private Dictionary<Registers, PCCRegister> pccRegs;

        private class PCCRegister
        {
            public PCCRegister(ushort offset, bool isPresent = true, bool isEnabled = false)
            {
                Offset = offset;
                IsPresent = isPresent;
                IsEnabled = isEnabled;
            }
            public bool IsPresent { get; }
            public bool IsEnabled { get; set; }
            public ushort Offset { get; }
        }

        private enum Registers
        {
            // This is called FTMC on S32K144W
            FTFC        = 0x80,
            DMAMUX      = 0x84,
            FlexCAN0    = 0x90,
            FlexCAN1    = 0x94,
            FTM3        = 0x98,
            ADC1        = 0x9C,
            FlexCAN2    = 0xAC,
            LPSPI0      = 0xB0,
            LPSPI1      = 0xB4,
            LPSPI2      = 0xB8,
            PDB1        = 0xC4,
            CRC         = 0xC8,
            PDB0        = 0xD8,
            LPIT        = 0xDC,
            FTM0        = 0xE0,
            FTM1        = 0xE4,
            FTM2        = 0xE8,
            ADC0        = 0xEC,
            RTC         = 0xF4,
            CMU0       = 0xF8, // Only available on S32K11x
            CMU1       = 0xFC, // Only available on S32K11x
            LPTMR0      = 0x100,
            PORTA       = 0x124,
            PORTB       = 0x128,
            PORTC       = 0x12C,
            PORTD       = 0x130,
            PORTE       = 0x134,
            SAI0        = 0x150,
            SAI1        = 0x154,
            FlexIO      = 0x168,
            EWM         = 0x184,
            LPI2C0      = 0x198,
            LPI2C1      = 0x19C,
            LPUART0     = 0x1A8,
            LPUART1     = 0x1AC,
            LPUART2     = 0x1B0,
            FTM4        = 0x1B8,
            FTM5        = 0x1BC,
            FTM6        = 0x1C0,
            FTM7        = 0x1C4,
            CMP0        = 0x1CC,
            QSPI        = 0x1D8,
            ENET        = 0x1E4
        }
    }
}
