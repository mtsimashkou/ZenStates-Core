using OpenLibSys;
using System;
using System.Threading;

namespace ZenStates.Core
{
    public class Cpu : IDisposable
    {
        private bool disposedValue;
        private static Mutex amdSmuMutex;
        private const ushort SMU_TIMEOUT = 8192;
        private const string InitializationExceptionText = "CPU module initialization failed.";

        public const uint F17H_M01H_SVI = 0x0005A000;
        public const uint F17H_M60H_SVI = 0x0006F000; // Renoir only?
        public const uint F17H_M01H_SVI_TEL_PLANE0 = (F17H_M01H_SVI + 0xC);
        public const uint F17H_M01H_SVI_TEL_PLANE1 = (F17H_M01H_SVI + 0x10);
        public const uint F17H_M30H_SVI_TEL_PLANE0 = (F17H_M01H_SVI + 0x14);
        public const uint F17H_M30H_SVI_TEL_PLANE1 = (F17H_M01H_SVI + 0x10);
        public const uint F17H_M60H_SVI_TEL_PLANE0 = (F17H_M60H_SVI + 0x38);
        public const uint F17H_M60H_SVI_TEL_PLANE1 = (F17H_M60H_SVI + 0x3C);
        public const uint F17H_M70H_SVI_TEL_PLANE0 = (F17H_M01H_SVI + 0x10);
        public const uint F17H_M70H_SVI_TEL_PLANE1 = (F17H_M01H_SVI + 0xC);
        public const uint F19H_M21H_SVI_TEL_PLANE0 = (F17H_M01H_SVI + 0x10);
        public const uint F19H_M21H_SVI_TEL_PLANE1 = (F17H_M01H_SVI + 0xC);

        public enum Family
        {
            UNSUPPORTED = 0x0,
            FAMILY_17H = 0x17,
            FAMILY_18H = 0x18,
            FAMILY_19H = 0x19,
        };

        public enum CodeName : int
        {
            Unsupported = 0,
            DEBUG,
            SummitRidge,
            Whitehaven,
            Naples,
            RavenRidge,
            PinnacleRidge,
            Colfax,
            Picasso,
            FireFlight,
            Matisse,
            CastlePeak,
            Rome,
            Dali,
            Renoir,
            VanGogh,
            Vermeer,
            Chagall,
            Milan,
            Cezanne,
            Rembrandt,
        };


        // CPUID_Fn80000001_EBX [BrandId Identifier] (BrandId)
        // [31:28] PkgType: package type.
        // Socket FP5/FP6 = 0
        // Socket AM4 = 2
        // Socket SP3 = 4
        // Socket TR4/TRX4 (SP3r2/SP3r3) = 7
        public enum PackageType : int
        {
            FPX = 0,
            AM4 = 2,
            SP3 = 4,
            TRX = 7,
        }

        public readonly Utils utils = new Utils();
        public readonly CPUInfo info = new CPUInfo();
        public readonly Ols Ols;
        public readonly SMU smu;

        public struct SVI2
        {
            public uint CoreAddress;
            public uint SocAddress;
        }

        public struct CPUInfo
        {
            public uint cpuid;
            public Family family;
            public CodeName codeName;
            public string cpuName;
            public PackageType packageType;
            public uint baseModel;
            public uint extModel;
            public uint model;
            public uint ccds;
            public uint ccxs;
            public uint coresPerCcx;
            public uint cores;
            public uint logicalCores;
            public uint physicalCores;
            public uint threadsPerCore;
            public uint patchLevel;
            public uint coreDisableMap;
            public SVI2 SVI2;
        }

        public Utils.LibStatus Status { get; private set; } = Utils.LibStatus.INITIALIZE_ERROR;

        public Cpu()
        {
            amdSmuMutex = new Mutex();
            Ols = new Ols();
            CheckOlsStatus();

            uint eax = 0, ebx = 0, ecx = 0, edx = 0;
            uint ccdsPresent = 0, ccdsDown = 0, coreDisableMap = 0;
            uint fuse1 = 0x5D218;
            //uint fuse2 = 0x5D21C;
            uint offset = 0x238;
            uint ccxPerCcd = 2;

            if (Ols.Cpuid(0x00000001, ref eax, ref ebx, ref ecx, ref edx) == 1)
            {
                info.cpuid = eax;
                info.family = (Family)(utils.GetBits(eax, 8, 4) + utils.GetBits(eax, 20, 8));
                info.baseModel = (eax & 0xf0) >> 4;
                info.extModel = (eax & 0xf0000) >> 12;
                info.model = info.baseModel + info.extModel;
                info.logicalCores = utils.GetBits(ebx, 16, 8);
            }
            else
            {
                throw new ApplicationException(InitializationExceptionText);
            }

            // Package type
            if (Ols.Cpuid(0x80000001, ref eax, ref ebx, ref ecx, ref edx) == 1)
            {
                info.packageType = (PackageType)(ebx >> 28);
                info.codeName = GetCodeName(info);
                smu = GetMaintainedSettings.GetByType(info.codeName);
                smu.Version = GetSmuVersion();
                smu.TableVersion = GetTableVersion();
            } 
            else
            {
                throw new ApplicationException(InitializationExceptionText);
            }

            info.cpuName = GetCpuName();

            if (Ols.Cpuid(0x8000001E, ref eax, ref ebx, ref ecx, ref edx) == 1)
            {
                info.threadsPerCore = utils.GetBits(ebx, 8, 4) + 1;
                if (info.threadsPerCore == 0)
                    info.cores = info.logicalCores;
                else
                    info.cores = info.logicalCores / info.threadsPerCore;
            }
            else
            {
                throw new ApplicationException(InitializationExceptionText);
            }

            // Get CCD and CCX configuration
            // https://gitlab.com/leogx9r/ryzen_smu/-/blob/master/userspace/monitor_cpu.c
            if (info.family == Family.FAMILY_19H)
            {
                //fuse1 += 0x10;
                //fuse2 += 0x10;
                offset = 0x598;
                ccxPerCcd = 1;
            }
            else if (info.family == Family.FAMILY_17H && info.model != 0x71 && info.model != 0x31)
            {
                fuse1 += 0x40;
                //fuse2 += 0x40;
            }

            if (!SmuReadReg(fuse1, ref ccdsPresent)/* || !SmuReadReg(fuse2, ref ccdsDown)*/)
                throw new ApplicationException(InitializationExceptionText);

            uint ccdEnableMap = utils.GetBits(ccdsPresent, 22, 8);
            //uint ccdDisableMap = utils.GetBits(ccdsPresent, 30, 2) | (utils.GetBits(ccdsDown, 0, 6) << 2);

            uint coreDisableMapAddress = (0x30081800 + offset) | ((ccdEnableMap & 1) == 0 ? 0x2000000 : 0u);

            if (!SmuReadReg(coreDisableMapAddress, ref coreDisableMap))
                throw new ApplicationException(InitializationExceptionText);

            info.coresPerCcx = (8 - utils.CountSetBits(coreDisableMap & 0xff)) / ccxPerCcd;
            info.ccds = utils.CountSetBits(ccdEnableMap);
            info.ccxs = info.ccds * ccxPerCcd;
            info.physicalCores = info.ccxs * 8 / ccxPerCcd;
            info.coreDisableMap = coreDisableMap;
            info.patchLevel = GetPatchLevel();
            info.SVI2 = GetSVI2Info(info.codeName);

            //if (!SendTestMessage())
            //    throw new ApplicationException("SMU is not responding");

            Status = Utils.LibStatus.OK;
        }

        private void CheckOlsStatus()
        {
            // Check support library status
            switch (Ols.GetStatus())
            {
                case (uint)Ols.Status.NO_ERROR:
                    break;
                case (uint)Ols.Status.DLL_NOT_FOUND:
                    throw new ApplicationException("WinRing DLL_NOT_FOUND");
                case (uint)Ols.Status.DLL_INCORRECT_VERSION:
                    throw new ApplicationException("WinRing DLL_INCORRECT_VERSION");
                case (uint)Ols.Status.DLL_INITIALIZE_ERROR:
                    throw new ApplicationException("WinRing DLL_INITIALIZE_ERROR");
            }

            // Check WinRing0 status
            switch (Ols.GetDllStatus())
            {
                case (uint)Ols.OlsDllStatus.OLS_DLL_NO_ERROR:
                    break;
                case (uint)Ols.OlsDllStatus.OLS_DLL_DRIVER_NOT_LOADED:
                    throw new ApplicationException("WinRing OLS_DRIVER_NOT_LOADED");
                case (uint)Ols.OlsDllStatus.OLS_DLL_UNSUPPORTED_PLATFORM:
                    throw new ApplicationException("WinRing OLS_UNSUPPORTED_PLATFORM");
                case (uint)Ols.OlsDllStatus.OLS_DLL_DRIVER_NOT_FOUND:
                    throw new ApplicationException("WinRing OLS_DLL_DRIVER_NOT_FOUND");
                case (uint)Ols.OlsDllStatus.OLS_DLL_DRIVER_UNLOADED:
                    throw new ApplicationException("WinRing OLS_DLL_DRIVER_UNLOADED");
                case (uint)Ols.OlsDllStatus.OLS_DLL_DRIVER_NOT_LOADED_ON_NETWORK:
                    throw new ApplicationException("WinRing DRIVER_NOT_LOADED_ON_NETWORK");
                case (uint)Ols.OlsDllStatus.OLS_DLL_UNKNOWN_ERROR:
                    throw new ApplicationException("WinRing OLS_DLL_UNKNOWN_ERROR");
            }
        }

        public bool SmuWriteReg(uint addr, uint data)
        {
            bool res = false;
            amdSmuMutex.WaitOne(5000);
            if (Ols.WritePciConfigDwordEx(smu.SMU_PCI_ADDR, smu.SMU_OFFSET_ADDR, addr) == 1)
                res = (Ols.WritePciConfigDwordEx(smu.SMU_PCI_ADDR, smu.SMU_OFFSET_DATA, data) == 1);
            amdSmuMutex.ReleaseMutex();
            return res;
        }

        public bool SmuReadReg(uint addr, ref uint data)
        {
            bool res = false;
            amdSmuMutex.WaitOne(5000);
            if (Ols.WritePciConfigDwordEx(smu.SMU_PCI_ADDR, smu.SMU_OFFSET_ADDR, addr) == 1)
                res = (Ols.ReadPciConfigDwordEx(smu.SMU_PCI_ADDR, smu.SMU_OFFSET_DATA, ref data) == 1);
            amdSmuMutex.ReleaseMutex();
            return res;
        }

        public bool SmuWaitDone()
        {
            bool res;
            ushort timeout = SMU_TIMEOUT;
            uint data = 0;

            do
                res = SmuReadReg(smu.SMU_ADDR_RSP, ref data);
            while ((!res || data != 1) && --timeout > 0);

            if (timeout == 0 || data != 1) res = false;

            return res;
        }

        public SMU.Status SendSmuCommand(uint msg, ref uint[] args)
        {
            ushort timeout = SMU_TIMEOUT;
            uint[] cmdArgs = new uint[6];
            int argsLength = args.Length;
            uint status = 0;

            if (argsLength > cmdArgs.Length)
                argsLength = cmdArgs.Length;

            for (int i = 0; i < argsLength; ++i)
                cmdArgs[i] = args[i];

            //if (amdSmuMutex.WaitOne(5000))
            {
                // Clear response register
                bool temp;
                do
                    temp = SmuWriteReg(smu.SMU_ADDR_RSP, 0);
                while ((!temp) && --timeout > 0);

                if (timeout == 0)
                {
                    //amdSmuMutex.ReleaseMutex();
                    SmuReadReg(smu.SMU_ADDR_RSP, ref status);
                    return (SMU.Status)status;
                }

                // Write data
                for (int i = 0; i < cmdArgs.Length; ++i)
                    SmuWriteReg(smu.SMU_ADDR_ARG + (uint)(i * 4), cmdArgs[i]);

                // Send message
                SmuWriteReg(smu.SMU_ADDR_MSG, msg);

                // Wait done
                if (!SmuWaitDone())
                {
                    //amdSmuMutex.ReleaseMutex();
                    SmuReadReg(smu.SMU_ADDR_RSP, ref status);
                    return (SMU.Status)status;
                }

                // Read back args
                for (int i = 0; i < args.Length; ++i)
                    SmuReadReg(smu.SMU_ADDR_ARG + (uint)(i * 4), ref args[i]);
            }

            //amdSmuMutex.ReleaseMutex();
            SmuReadReg(smu.SMU_ADDR_RSP, ref status);

            return (SMU.Status)status;
        }

        // Legacy
        public bool SendSmuCommand(uint msg, uint arg)
        {
            uint[] args = { arg };
            return SendSmuCommand(msg, ref args) == SMU.Status.OK;
        }

        public uint ReadDword(uint value)
        {
            amdSmuMutex.WaitOne(5000);
            Ols.WritePciConfigDword(smu.SMU_PCI_ADDR, (byte)smu.SMU_OFFSET_ADDR, value);
            uint res = Ols.ReadPciConfigDword(smu.SMU_PCI_ADDR, (byte)smu.SMU_OFFSET_DATA);
            amdSmuMutex.ReleaseMutex();

            return res;
        }

        public double GetCoreMulti(int index = 0)
        {
            uint eax = default, edx = default;
            if (Ols.RdmsrTx(0xC0010293, ref eax, ref edx, (UIntPtr)(1 << index)) != 1)
                return 0;

            double multi = 25 * (eax & 0xFF) / (12.5 * (eax >> 8 & 0x3F));
            return Math.Round(multi * 4, MidpointRounding.ToEven) / 4;
        }

        public bool WriteMsr(uint msr, uint eax, uint edx)
        {
            bool res = true;

            for (var i = 0; i < info.logicalCores; i++)
            {
                if (Ols.WrmsrTx(msr, eax, edx, (UIntPtr)(1 << i)) != 1) res = false;
            }

            return res;
        }

        // https://en.wikichip.org/wiki/amd/cpuid
        public CodeName GetCodeName(CPUInfo cpuInfo)
        {
            CodeName codeName = CodeName.Unsupported;

            if (cpuInfo.family == Family.FAMILY_17H)
            {
                switch (cpuInfo.model)
                {
                    // Zen
                    case 0x1:
                        if (cpuInfo.packageType == PackageType.SP3)
                            codeName = CodeName.Naples;
                        else if (cpuInfo.packageType == PackageType.TRX)
                            codeName = CodeName.Whitehaven;
                        else
                            codeName = CodeName.SummitRidge;
                        break;
                    case 0x11:
                        codeName = CodeName.RavenRidge;
                        break;
                    case 0x20:
                        codeName = CodeName.Dali;
                        break;
                    // Zen+
                    case 0x8:
                        if (cpuInfo.packageType == PackageType.SP3 || cpuInfo.packageType == PackageType.TRX)
                            codeName = CodeName.Colfax;
                        else
                            codeName = CodeName.PinnacleRidge;
                        break;
                    case 0x18:
                        codeName = CodeName.Picasso;
                        break;
                    case 0x50: // Subor Z+, CPUID 0x00850F00
                        codeName = CodeName.FireFlight;
                        break;
                    // Zen2
                    case 0x31:
                        if (cpuInfo.packageType == PackageType.TRX)
                            codeName = CodeName.CastlePeak;
                        else
                            codeName = CodeName.Rome;
                        break;
                    case 0x60:
                        codeName = CodeName.Renoir;
                        break;
                    case 0x71:
                        codeName = CodeName.Matisse;
                        break;
                    case 0x90:
                        codeName = CodeName.VanGogh;
                        break;

                    default:
                        codeName = CodeName.Unsupported;
                        break;
                }
            }
            else if (cpuInfo.family == Family.FAMILY_19H)
            {
                switch (cpuInfo.model)
                {
                    // Does Chagall (Zen3 TR) has different model number than Milan?
                    case 0x0:
                        if (cpuInfo.packageType == PackageType.TRX)
                            codeName = CodeName.Chagall;
                        else
                            codeName = CodeName.Milan;
                        break;
                    case 0x21:
                        codeName = CodeName.Vermeer;
                        break;
                    case 0x40:
                        codeName = CodeName.Rembrandt;
                        break;
                    case 0x50:
                        codeName = CodeName.Cezanne;
                        break;

                    default:
                        codeName = CodeName.Unsupported;
                        break;
                }
            }

            return codeName;
        }

        // SVI2 interface
        public SVI2 GetSVI2Info(CodeName codeName)
        {
            SVI2 svi = new SVI2();
 
            switch (codeName)
            {
                //Zen, Zen+
                case CodeName.SummitRidge:
                case CodeName.PinnacleRidge:
                case CodeName.RavenRidge:
                case CodeName.FireFlight:
                case CodeName.Dali:
                    svi.CoreAddress = F17H_M01H_SVI_TEL_PLANE0;
                    svi.SocAddress = F17H_M01H_SVI_TEL_PLANE1;
                    break;

                // Zen Threadripper/EPYC
                case CodeName.Whitehaven:
                case CodeName.Naples:
                case CodeName.Colfax:
                    svi.CoreAddress = F17H_M01H_SVI_TEL_PLANE1;
                    svi.SocAddress = F17H_M01H_SVI_TEL_PLANE0;
                    break;

                // Zen2 Threadripper/EPYC
                case CodeName.CastlePeak:
                case CodeName.Rome:
                    svi.CoreAddress = F17H_M30H_SVI_TEL_PLANE0;
                    svi.SocAddress = F17H_M30H_SVI_TEL_PLANE1;
                    break;

                // Picasso
                case CodeName.Picasso:
                    if ((smu.Version & 0xFF000000) > 0)
                    {
                        svi.CoreAddress = F17H_M01H_SVI_TEL_PLANE0;
                        svi.SocAddress = F17H_M01H_SVI_TEL_PLANE1;
                    }
                    else
                    {
                        svi.CoreAddress = F17H_M01H_SVI_TEL_PLANE1;
                        svi.SocAddress = F17H_M01H_SVI_TEL_PLANE0;
                    }
                    break;

                // Zen2
                case CodeName.Matisse:
                    svi.CoreAddress = F17H_M70H_SVI_TEL_PLANE0;
                    svi.SocAddress = F17H_M70H_SVI_TEL_PLANE1;
                    break;

                // Zen2 APU, Zen3 APU ?
                case CodeName.Renoir:
                    //case Cpu.CodeName.VanGogh:
                    //case Cpu.CodeName.Cezanne:
                    svi.CoreAddress = F17H_M60H_SVI_TEL_PLANE0;
                    svi.SocAddress = F17H_M60H_SVI_TEL_PLANE1;
                    break;

                // Zen3, Zen3 Threadripper/EPYC ?
                case CodeName.Vermeer:
                    //case Cpu.CodeName.Chagall:
                    //case Cpu.CodeName.Milan:
                    svi.CoreAddress = F19H_M21H_SVI_TEL_PLANE0;
                    svi.SocAddress = F19H_M21H_SVI_TEL_PLANE1;
                    break;

                default:
                    svi.CoreAddress = F17H_M01H_SVI_TEL_PLANE0;
                    svi.SocAddress = F17H_M01H_SVI_TEL_PLANE1;
                    break;
            }

            return svi;
        }

        public string GetCpuName()
        {
            string model = "";
            uint eax = 0, ebx = 0, ecx = 0, edx = 0;

            if (Ols.Cpuid(0x80000002, ref eax, ref ebx, ref ecx, ref edx) == 1)
                model = model + utils.IntToStr(eax) + utils.IntToStr(ebx) + utils.IntToStr(ecx) + utils.IntToStr(edx);

            if (Ols.Cpuid(0x80000003, ref eax, ref ebx, ref ecx, ref edx) == 1)
                model = model + utils.IntToStr(eax) + utils.IntToStr(ebx) + utils.IntToStr(ecx) + utils.IntToStr(edx);

            if (Ols.Cpuid(0x80000004, ref eax, ref ebx, ref ecx, ref edx) == 1)
                model = model + utils.IntToStr(eax) + utils.IntToStr(ebx) + utils.IntToStr(ecx) + utils.IntToStr(edx);

            return model.Trim();
        }

        public int GetCpuNodes()
        {
            uint eax = 0, ebx = 0, ecx = 0, edx = 0;
            if (Ols.Cpuid(0x8000001E, ref eax, ref ebx, ref ecx, ref edx) == 1)
            {
                return Convert.ToInt32(ecx >> 8 & 0x7) + 1;
            }
            return 1;
        }

        public uint GetSmuVersion()
        {
            uint[] args = new uint[6];
            if (SendSmuCommand(smu.SMU_MSG_GetSmuVersion, ref args) == SMU.Status.OK)
                return args[0];

            return 0;
        }

        public uint GetPatchLevel()
        {
            uint eax = 0, edx = 0;
            if (Ols.Rdmsr(0x8b, ref eax, ref edx) != 1)
                return 0;

            return eax;
        }

        public bool GetOcMode()
        {
            if (info.codeName == CodeName.SummitRidge)
            {
                uint eax = 0;
                uint edx = 0;

                if (Ols.Rdmsr(0xC0010063, ref eax, ref edx) == 1)
                {
                    // Summit Ridge, Raven Ridge
                    return Convert.ToBoolean((eax >> 1) & 1);
                }
                return false;
            }

            return GetPBOScalar() == 0;
        }

        public float GetPBOScalar()
        {
            uint[] args = new uint[6];
            if (SendSmuCommand(smu.SMU_MSG_GetPBOScalar, ref args) == SMU.Status.OK)
            {
                byte[] bytes = BitConverter.GetBytes(args[0]);
                float scalar = BitConverter.ToSingle(bytes, 0);

                if (scalar > 0)
                    return scalar;
            }
            return 0f;
        }

        public SMU.Status TransferTableToDram()
        {
            uint[] args = { 1, 1, 0, 0, 0, 0 };

            if (smu.SMU_TYPE == SMU.SmuType.TYPE_APU0)
            {
                args[0] = 3;
                args[1] = 0;
            }

            return SendSmuCommand(smu.SMU_MSG_TransferTableToDram, ref args);
        }

        public uint GetTableVersion()
        {
            uint[] args = new uint[6];

            SMU.Status status = SendSmuCommand(smu.SMU_MSG_GetTableVersion, ref args);

            if (status == SMU.Status.OK)
                return args[0];

            return 0;
        }

        public ulong GetDramBaseAddress()
        {
            uint[] args = new uint[6];
            ulong address = 0;

            SMU.Status status = SMU.Status.FAILED;

            switch (smu.SMU_TYPE)
            {
                // SummitRidge, PinnacleRidge, Colfax
                case SMU.SmuType.TYPE_CPU0:
                case SMU.SmuType.TYPE_CPU1:
                    args[0] = 0;
                    status = SendSmuCommand(smu.SMU_MSG_GetDramBaseAddress - 1, ref args);
                    if (status != SMU.Status.OK)
                        return 0;

                    status = SendSmuCommand(smu.SMU_MSG_GetDramBaseAddress, ref args);
                    if (status != SMU.Status.OK)
                        return 0;

                    address = args[0];

                    args[0] = 0;
                    status = SendSmuCommand(smu.SMU_MSG_GetDramBaseAddress + 2, ref args);
                    if (status != SMU.Status.OK)
                        return 0;
                    break;

                // Matisse, CastlePeak, Rome, Vermeer, Chagall?, Milan?
                case SMU.SmuType.TYPE_CPU2:
                case SMU.SmuType.TYPE_CPU3:
                    status = SendSmuCommand(smu.SMU_MSG_GetDramBaseAddress, ref args);
                    if (status != SMU.Status.OK)
                        return 0;
                    address = args[0] | ((ulong)args[1] << 32);
                    break;

                // Renoir
                case SMU.SmuType.TYPE_APU1:
                    status = SendSmuCommand(smu.SMU_MSG_GetDramBaseAddress, ref args);
                    if (status != SMU.Status.OK)
                        return 0;
                    address = args[0] | ((ulong)args[1] << 32);
                    break;

                // RavenRidge, RavenRidge2, Picasso
                case SMU.SmuType.TYPE_APU0:
                    uint[] parts = new uint[2];

                    args[0] = 3;
                    status = SendSmuCommand(smu.SMU_MSG_GetDramBaseAddress - 1, ref args);
                    if (status != SMU.Status.OK)
                        return 0;

                    args[0] = 3;
                    status = SendSmuCommand(smu.SMU_MSG_GetDramBaseAddress, ref args);
                    if (status != SMU.Status.OK)
                        return 0;

                    // First base
                    parts[0] = args[0];

                    args[0] = 5;
                    status = SendSmuCommand(smu.SMU_MSG_GetDramBaseAddress - 1, ref args);
                    if (status != SMU.Status.OK)
                        return 0;

                    status = SendSmuCommand(smu.SMU_MSG_GetDramBaseAddress, ref args);
                    if (status != SMU.Status.OK)
                        return 0;

                    // Second base
                    parts[1] = args[0];
                    address = (ulong)parts[1] << 32 | parts[0];
                    break;

                default:
                    break;
            }

            if (status == SMU.Status.OK)
                return address;

            return 0;
        }

        public bool SendTestMessage()
        {
            uint[] args = new uint[6];
            return SendSmuCommand(smu.SMU_MSG_TestMessage, ref args) == SMU.Status.OK;
        }

        public bool IsProchotEnabled()
        {
            uint data = ReadDword(0x59804);
            return (data & 1) == 1;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    try
                    {
                        amdSmuMutex.ReleaseMutex();
                    }
                    catch { }

                    utils.Dispose();
                    Ols.DeinitializeOls();
                    Ols.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
