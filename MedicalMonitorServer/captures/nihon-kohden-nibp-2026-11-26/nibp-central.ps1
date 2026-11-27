# Present ourselves to the BSM-3562 as a monitoring CENTRAL: send the 7 per-bed requests the real
# CNS-9101 sends (table @exe 0x0070DC00), especially 0206 "MonitorBed" which we never sent before.
# Record every response so we can see whether the monitor now engages us as a central.
param(
    [string]$LocalIp   = '10.200.200.77',
    [string]$MonitorIp = '10.10.46.48',
    [int]$Seconds = 30,
    [int]$CandTypeHi = 0,
    [int]$CandTypeLo = 0,
    [int]$CandKindLo = 0,
    [string]$CandPayloadHex = '',
    [int]$CandSendAt = 12,
    [string]$Out = "$PSScriptRoot\central.raw.txt"
)
$cand = @()
if ($CandPayloadHex -ne '') { $cand = @($CandPayloadHex -split '[ ,]+' | Where-Object { $_ -ne '' } | ForEach-Object { [byte]('0x'+$_) }) }
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

public static class NibpCentral {
    static string Hex(byte[] b,int n){var h=new StringBuilder(n*3);for(int i=0;i<n;i++)h.Append(b[i].ToString("X2")).Append(' ');return h.ToString();}
    static byte[] Msg(byte kindLow,ushort seq,byte tHi,byte tLo,byte[] id,byte[] ip,byte[] payload){
        int len=32+payload.Length;var b=new byte[len];
        b[1]=kindLow;b[2]=(byte)(seq>>8);b[3]=(byte)seq;b[4]=(byte)(len>>8);b[5]=(byte)len;b[7]=1;b[9]=1;
        b[12]=tHi;b[13]=tLo;b[16]=(byte)((len-12)>>8);b[17]=(byte)(len-12);
        id.CopyTo(b,24);ip.CopyTo(b,28);payload.CopyTo(b,32);return b;}

    public static void Run(int seconds,string localIp,string monitorIp,string path,byte candKindLo,byte candHi,byte candLo,byte[] cand,int candAt){
        var local=IPAddress.Parse(localIp);var monitor=IPAddress.Parse(monitorIp);
        var a=local.GetAddressBytes();var m=monitor.GetAddressBytes();
        var monId=new byte[]{m[1],m[2],m[3],0x20};

        var beacon=new byte[64];
        new byte[]{0x04,0xFF,0,0,0,0x40,0,1,0,1,0,0,0x02,0x01,0,0,0,0x34}.CopyTo(beacon,0);
        beacon[24]=a[1];beacon[25]=a[2];beacon[26]=a[3];a.CopyTo(beacon,28);beacon[33]=0x01;
        Encoding.ASCII.GetBytes("CENTRAL").CopyTo(beacon,36);
        a.CopyTo(beacon,48);beacon[52]=a[1];beacon[53]=a[2];beacon[54]=a[3];beacon[55]=0x00;
        beacon[60]=0x01;beacon[61]=0x86;beacon[62]=0x01;

        var subscribe=new byte[]{4,0,0,0,0,0,1,0,8,0,1,1,1,0,0,0,0x55,0x55,0x55,0x7F,1,0,0,0,0x55,0x55,0x55,0x57};
        var bare=new byte[]{0x06,0xFF,0,0,0,0x0C,0,1,0,1,0,0};

        // the 7 central per-bed requests (type,subtype,b2) from exe table 0x0070DC00
        byte[][] reqs = new byte[][]{
            new byte[]{0x02,0x06,0x07}, new byte[]{0x03,0x08,0x09}, new byte[]{0x03,0x04,0x05},
            new byte[]{0x03,0x01,0x02}, new byte[]{0x04,0x04,0x05}, new byte[]{0x04,0x01,0x02},
            new byte[]{0x03,0x20,0x21}
        };

        var control=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);control.EnableBroadcast=true;control.Bind(new IPEndPoint(local,55405));
        var in8300=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);in8300.EnableBroadcast=true;in8300.Bind(new IPEndPoint(IPAddress.Any,8300));
        var in8301=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);in8301.Bind(new IPEndPoint(IPAddress.Any,8301));
        var socks=new[]{control,in8300,in8301};
        var bcast=new[]{new IPEndPoint(IPAddress.Parse("10.255.255.255"),8300),new IPEndPoint(IPAddress.Broadcast,8300)};
        var monCtl=new IPEndPoint(monitor,8300);

        var buf=new byte[65536];ushort seq=0x0700;
        var start=DateTime.UtcNow;var end=start.AddSeconds(seconds);
        var nextBeacon=start;var nextSub=start.AddSeconds(2);var nextReg=start.AddSeconds(3);var lastStream=DateTime.MinValue;
        bool candSent=false;

        using(var raw=new StreamWriter(path,false){AutoFlush=true}){
            raw.WriteLine("# central-presentation probe: sending 7 per-bed requests incl 0206 MonitorBed");
            while(DateTime.UtcNow<end){
                var now=DateTime.UtcNow;
                if(now>=nextBeacon){foreach(var t in bcast){try{control.SendTo(beacon,t);}catch{}}nextBeacon=now.AddSeconds(3);}
                if(now>=nextSub&&(now-lastStream).TotalSeconds>2){control.SendTo(Msg(0,++seq,0x02,0x22,monId,a,subscribe),monCtl);nextSub=now.AddSeconds(1);}
                if(now>=nextReg){
                    foreach(var r in reqs){
                        // try payload = empty, plus the b2 as a 4-byte little count (central style)
                        var pay=new byte[]{r[2],0,0,0};
                        var pkt=Msg((byte)(r[0]==0x03?1:0),++seq,r[0],r[1],monId,a,pay);
                        control.SendTo(pkt,monCtl);
                        raw.WriteLine(now.ToLocalTime().ToString("HH:mm:ss.fff")+" REQ "+r[0].ToString("X2")+r[1].ToString("X2")+" "+Hex(pkt,pkt.Length));
                    }
                    nextReg=now.AddSeconds(2);
                }
                if(!candSent && cand!=null && cand.Length>0 && (now-start).TotalSeconds>=candAt){
                    var pkt=Msg(candKindLo,++seq,candHi,candLo,monId,a,cand);
                    control.SendTo(pkt,monCtl);
                    raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff")+" CAND "+candHi.ToString("X2")+candLo.ToString("X2")+" "+Hex(pkt,pkt.Length));
                    candSent=true;
                }
                foreach(var s in socks){
                    try{while(s.Available>0){
                        EndPoint ep=new IPEndPoint(IPAddress.Any,0);
                        int n=s.ReceiveFrom(buf,ref ep);var from=(IPEndPoint)ep;
                        if(!from.Address.Equals(monitor))continue;
                        if(n==64&&buf[0]==0x04)continue;
                        int port=((IPEndPoint)s.LocalEndPoint).Port;
                        if(buf[0]==0x0B||buf[0]==0x12)lastStream=DateTime.UtcNow;
                        if(buf[0]==0x05)control.SendTo(bare,monCtl);
                        raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff")+" IN "+from.Port+">"+port+" "+n+" "+Hex(buf,n));
                    }}catch{}
                }
                System.Threading.Thread.Sleep(5);
            }
            control.SendTo(Msg(0,++seq,0x02,0x22,monId,a,new byte[16]),monCtl);
        }
        foreach(var s in socks)s.Close();
    }
}
'@
"central-presentation probe -> $Out ($Seconds s)  cand=$('{0:X2}{1:X2}' -f $CandTypeHi,$CandTypeLo) [$CandPayloadHex]@${CandSendAt}s"
[NibpCentral]::Run($Seconds,$LocalIp,$MonitorIp,$Out,[byte]$CandKindLo,[byte]$CandTypeHi,[byte]$CandTypeLo,[byte[]]$cand,$CandSendAt)
"done"
