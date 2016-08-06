using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;

namespace NfcPcSc
{
    class NfcMain
    {
        public void start()
        {
            IntPtr context = establishContext();

            List<string> readersList = getReaders(context);

            Console.WriteLine(readersList.Count + "台のリーダーがあります。");
            foreach (string s in readersList){
                Console.WriteLine(s);
            }

            NfcApi.SCARD_READERSTATE[] readerStateArray = initializeReaderState(context, readersList);

            bool quit = false;
            while (!quit)
            {
                waitReaderStatusChange(context, readerStateArray, 1000);
                if ((readerStateArray[0].dwEventState & NfcConstant.SCARD_STATE_PRESENT) == NfcConstant.SCARD_STATE_PRESENT)
                {
                    ReadResult result2 = readCard(context, readerStateArray[0].szReader);
                    SendCommand(context, readerStateArray[0].szReader);
                    quit = true;
                }
            }
            uint ret = NfcApi.SCardReleaseContext(context);
        }

        private IntPtr establishContext()
        {
            IntPtr context = IntPtr.Zero;

            uint ret = NfcApi.SCardEstablishContext(NfcConstant.SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out context);
            if (ret != NfcConstant.SCARD_S_SUCCESS)
            {
                string message;
                switch (ret)
                {
                    case NfcConstant.SCARD_E_NO_SERVICE:
                        message = "サービスが起動されていません。";
                        break;
                    default:
                        message = "Smart Cardサービスに接続できません。code = " + ret;
                        break;
                }
                System.Console.WriteLine(message);
                return IntPtr.Zero;
            }
            System.Console.WriteLine("Smart Cardサービスに接続しました。");
            return context;
        }

        List<string> getReaders(IntPtr hContext)
        {
            uint pcchReaders = 0;

            uint ret = NfcApi.SCardListReaders(hContext, null, null, ref pcchReaders);
            if (ret != NfcConstant.SCARD_S_SUCCESS)
            {
                System.Console.WriteLine("リーダーの情報が取得できません。その１: " + ret);
                return new List<string>();
            }

            byte[] mszReaders = new byte[pcchReaders * 2]; // 1文字2byte

            // Fill readers buffer with second call.
            ret = NfcApi.SCardListReaders(hContext, null, mszReaders, ref pcchReaders);
            if (ret != NfcConstant.SCARD_S_SUCCESS)
            {
                System.Console.WriteLine("リーダーの情報が取得できません。その２: "+ ret);
                return new List<string>();
            }

            UnicodeEncoding unicodeEncoding = new UnicodeEncoding();
            string readerNameMultiString = unicodeEncoding.GetString(mszReaders);

            System.Console.WriteLine("リーダー名を\\0で接続した文字列: " + readerNameMultiString);
            System.Console.WriteLine(" ");

            List<string> readersList = new List<string>();
            int nullindex = readerNameMultiString.IndexOf((char)0);   // 装置は１台のみ
            readersList.Add(readerNameMultiString.Substring(0, nullindex));
            return readersList;
        }

        NfcApi.SCARD_READERSTATE[] initializeReaderState(IntPtr hContext, List<string> readerNameList)
        {
            NfcApi.SCARD_READERSTATE[] readerStateArray = new NfcApi.SCARD_READERSTATE[readerNameList.Count];
            int i = 0;
            foreach (string readerName in readerNameList)
            {
                readerStateArray[i].dwCurrentState = NfcConstant.SCARD_STATE_UNAWARE;
                readerStateArray[i].szReader = readerName;
                i++;
            }
            uint ret = NfcApi.SCardGetStatusChange(hContext, 100/*msec*/, readerStateArray, readerStateArray.Length);
            if (ret != NfcConstant.SCARD_S_SUCCESS)
            {
                throw new ApplicationException("リーダーの初期状態の取得に失敗。code = " + ret);
            }

            return readerStateArray;
        }

        void waitReaderStatusChange(IntPtr hContext, NfcApi.SCARD_READERSTATE[] readerStateArray, int timeoutMillis)
        {
            uint ret = NfcApi.SCardGetStatusChange(hContext, timeoutMillis/*msec*/, readerStateArray, readerStateArray.Length);
            switch (ret)
            {
                case NfcConstant.SCARD_S_SUCCESS:
                    break;
                case NfcConstant.SCARD_E_TIMEOUT:
                    throw new TimeoutException();
                default:
                    throw new ApplicationException("リーダーの状態変化の取得に失敗。code = " + ret);
            }

        }

        ReadResult readCard(IntPtr context, string readerName)
        {
            IntPtr hCard = connect(context, readerName);
            string readerSerialNumber = readReaderSerialNumber(hCard);
            string cardId = readCardId(hCard);
            System.Console.WriteLine(readerName + " (S/N " + readerSerialNumber + ") から、カードを読み取りました。" + cardId);
            disconnect(hCard);

            ReadResult result = new ReadResult();
            result.readerSerialNumber = readerSerialNumber;
            result.cardId = cardId;
            return result;

        }

        string readReaderSerialNumber(IntPtr hCard)
        {
            int controlCode = 0x003136b0; // SCARD_CTL_CODE(3500) の値 
            // IOCTL_PCSC_CCID_ESCAPE
            // SONY SDK for NFC M579_PC_SC_2.1j.pdf 3.1.1 IOCTRL_PCSC_CCID_ESCAPE
            byte[] sendBuffer = new byte[] { 0xc0, 0x08 }; // ESC_CMD_GET_INFO / Product Serial Number 
            byte[] recvBuffer = new byte[64];
            int recvLength = control(hCard, controlCode, sendBuffer, recvBuffer);

            ASCIIEncoding asciiEncoding = new ASCIIEncoding();
            string serialNumber = asciiEncoding.GetString(recvBuffer, 0, recvLength - 1); // recvBufferには\0で終わる文字列が取得されるので、長さを-1する。
            return serialNumber;
        }

        string readCardId(IntPtr hCard)
        {
            byte maxRecvDataLen = 64;
            byte[] recvBuffer = new byte[maxRecvDataLen + 2];
            byte[] sendBuffer = new byte[] { 0xff, 0xca, 0x00, 0x00, maxRecvDataLen };
            int recvLength = transmit(hCard, sendBuffer, recvBuffer);

            string cardId = BitConverter.ToString(recvBuffer, 0, recvLength - 2).Replace("-", "");
            return cardId;
        }

        void SendCommand(IntPtr hContext, string readerName)
        {
            int dwResponseSize;
            byte[] response = new byte[2048];
            long lResult;

            byte[] commnadSelectFile = { 0xff, 0xA4, 0x00, 0x01, 0x02, 0x0f, 0x09 };
            byte[] commnadReadBinary = { 0xff, 0xb0, 0x00, 0x00, 0x00 };

            IntPtr SCARD_PCI_T1 = getPciT1();
            NfcApi.SCARD_IO_REQUEST ioRecv = new NfcApi.SCARD_IO_REQUEST();
            ioRecv.cbPciLength = 2048;
            IntPtr hCard = connect(hContext, readerName);

            dwResponseSize = response.Length;
            lResult = NfcApi.SCardTransmit(hCard, SCARD_PCI_T1, commnadSelectFile, commnadSelectFile.Length, ioRecv, response, ref dwResponseSize);
            if (lResult != NfcConstant.SCARD_S_SUCCESS)
            {
                System.Console.WriteLine("SelectFile error\n");
                return;
            }
            dwResponseSize = response.Length;
            lResult = NfcApi.SCardTransmit(hCard, SCARD_PCI_T1, commnadReadBinary, commnadReadBinary.Length, ioRecv, response, ref dwResponseSize);
            if (lResult != NfcConstant.SCARD_S_SUCCESS)
            {
                System.Console.WriteLine("ReadBinary error\n");
                return;
            }
            parse_tag(response);
        }

        private void parse_tag(byte[] data)
        {
            int loop = 0;

            System.Console.WriteLine("\nSuica履歴データ：" + BitConverter.ToString(data, 0, 20) + "\n");

        }

        private IntPtr getPciT1()
        {
            IntPtr handle = NfcApi.LoadLibrary("Winscard.dll");
            IntPtr pci = NfcApi.GetProcAddress(handle, "g_rgSCardT1Pci");
            NfcApi.FreeLibrary(handle);
            return pci;
        }

        IntPtr connect(IntPtr hContext, string readerName)
        {
            IntPtr hCard = IntPtr.Zero;
            IntPtr activeProtocol = IntPtr.Zero;
            uint ret = NfcApi.SCardConnect(hContext, readerName, NfcConstant.SCARD_SHARE_SHARED, NfcConstant.SCARD_PROTOCOL_T1, ref hCard, ref activeProtocol);
            if (ret != NfcConstant.SCARD_S_SUCCESS)
            {
                throw new ApplicationException("カードに接続できません。code = " + ret);
            }
            return hCard;
        }

        void disconnect(IntPtr hCard)
        {
            uint ret = NfcApi.SCardDisconnect(hCard, NfcConstant.SCARD_LEAVE_CARD);
            if (ret != NfcConstant.SCARD_S_SUCCESS)
            {
                throw new ApplicationException("カードとの接続を切断できません。code = " + ret);
            }
        }

        int control(IntPtr hCard, int controlCode, byte[] sendBuffer, byte[] recvBuffer)
        {
            int bytesReturned = 0;
            uint ret = NfcApi.SCardControl(hCard, controlCode, sendBuffer, sendBuffer.Length, recvBuffer, recvBuffer.Length, ref bytesReturned);
            if (ret != NfcConstant.SCARD_S_SUCCESS)
            {
                throw new ApplicationException("カードへの制御命令送信に失敗しました。code = " + ret);
            }
            return bytesReturned;
        }

        int transmit(IntPtr hCard, byte[] sendBuffer, byte[] recvBuffer)
        {
            NfcApi.SCARD_IO_REQUEST ioRecv = new NfcApi.SCARD_IO_REQUEST();
            ioRecv.cbPciLength = 255;

            int pcbRecvLength = recvBuffer.Length;
            int cbSendLength = sendBuffer.Length;
            IntPtr SCARD_PCI_T1 = getPciT1();
            uint ret = NfcApi.SCardTransmit(hCard, SCARD_PCI_T1, sendBuffer, cbSendLength, ioRecv, recvBuffer, ref pcbRecvLength);
            if (ret != NfcConstant.SCARD_S_SUCCESS)
            {
                throw new ApplicationException("カードへの送信に失敗しました。code = " + ret);
            }
            return pcbRecvLength; // 受信したバイト数(recvBufferに受け取ったバイト数)
        }

    }

    class ReadResult
    {
        internal string cardId;
        internal string readerSerialNumber;
    }
}