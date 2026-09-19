// HL7 ORU reader for Mindray uMec10 monitors over LAN.
//
// How this monitor works (discovered by sniffing the wire):
//   * It broadcasts static "registration" beacons on UDP 3501/4600/3505
//     announcing itself and the server it wants (192.168.0.99). Those beacons
//     contain NO live vitals.
//   * The real vital-sign data (HL7 ORU^R01) is served over *TCP port 4601*.
//     As soon as a client connects, the monitor streams MLLP-framed HL7:
//        <VT> ...HL7 message... <FS><CR>   (VT=0x0B, FS=0x1C, CR=0x0D)
//
// So this program is a TCP *client*: it connects to the monitor, reads the
// ORU stream, prints it, and appends every message to a per-day log file
// (plus writes the newest message to latest.txt). It auto-reconnects.
//
// Note: parameter labels are Cyrillic (Windows-1251) because the monitor's
// UI language is Russian; the numeric values are plain ASCII. Files are saved
// as raw bytes so nothing is lost.
//
// Build:  g++ -O2 -o read read.cpp
// Run:    ./read                          (192.168.0.100:4601 -> ./hl7_messages)
//         ./read 192.168.0.100 4601 out   (custom monitor IP, port, folder)
//
// Network setup (direct cable to a USB-Ethernet adapter):
//   sudo nmcli device set <iface> managed no
//   sudo ip addr add 192.168.0.99/24 dev <iface>
//   sudo ip link set <iface> up

#include <arpa/inet.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <string>

static const unsigned char VT = 0x0B;  // MLLP start block
static const unsigned char FS = 0x1C;  // MLLP end block
static const unsigned char CR = 0x0D;  // carriage return

static std::string nowStr(const char* fmt) {
    time_t t = time(nullptr);
    struct tm tm;
    localtime_r(&t, &tm);
    char buf[64];
    strftime(buf, sizeof(buf), fmt, &tm);
    return std::string(buf);
}

// Field n (1-based) of the MSH segment.
static std::string mshField(const std::string& msg, int n) {
    size_t pos = msg.find("MSH");
    if (pos == std::string::npos) return "";
    size_t end = msg.find('\r', pos);
    if (end == std::string::npos) end = msg.size();
    std::string seg = msg.substr(pos, end - pos);
    int field = 1;
    size_t start = seg.find('|');
    if (start == std::string::npos) return "";
    ++start;
    while (field < n && start <= seg.size()) {
        size_t bar = seg.find('|', start);
        if (bar == std::string::npos)
            return (field == n) ? seg.substr(start) : "";
        if (field + 1 == n) return seg.substr(start, bar - start);
        start = bar + 1;
        ++field;
    }
    return "";
}

static void saveMessage(const std::string& outDir, const std::string& msg,
                        long seq) {
    std::string type = mshField(msg, 9);
    if (type.empty()) type = "MSG";

    // Readable form: HL7 segment separator \r -> \n.
    std::string pretty = msg;
    for (char& c : pretty)
        if (c == '\r') c = '\n';

    // Append to a per-day log.
    std::string logPath = outDir + "/hl7_" + nowStr("%Y-%m-%d") + ".log";
    FILE* lf = fopen(logPath.c_str(), "ab");
    if (lf) {
        fprintf(lf, "----- %s  #%ld  %s -----\n%s\n\n",
                nowStr("%Y-%m-%d %H:%M:%S").c_str(), seq, type.c_str(),
                pretty.c_str());
        fclose(lf);
    }

    // Always keep the newest message handy.
    std::string latest = outDir + "/latest.txt";
    FILE* nf = fopen(latest.c_str(), "wb");
    if (nf) {
        fwrite(pretty.data(), 1, pretty.size(), nf);
        fclose(nf);
    }

    printf("[%s] #%ld %s (%zu bytes)\n", nowStr("%H:%M:%S").c_str(), seq,
           type.c_str(), msg.size());
    fflush(stdout);
}

// Read the stream from a connected socket, splitting out MLLP messages.
// Returns when the connection drops.
static void pump(int fd, const std::string& outDir, long& seq) {
    std::string buf;
    char chunk[8192];
    ssize_t n;
    while ((n = recv(fd, chunk, sizeof(chunk), 0)) > 0) {
        buf.append(chunk, n);
        while (true) {
            size_t start = buf.find((char)VT);
            if (start == std::string::npos) {
                if (buf.size() > ((size_t)1 << 20)) buf.clear();
                break;
            }
            size_t end = buf.find((char)FS, start);
            if (end == std::string::npos) break;  // wait for the rest
            std::string msg = buf.substr(start + 1, end - start - 1);
            size_t consumed = end + 1;
            if (consumed < buf.size() && buf[consumed] == (char)CR) ++consumed;
            buf.erase(0, consumed);
            if (msg.find("MSH") != std::string::npos)
                saveMessage(outDir, msg, seq++);
        }
    }
}

int main(int argc, char** argv) {
    std::string ip = (argc > 1) ? argv[1] : "192.168.0.100";
    int port       = (argc > 2) ? atoi(argv[2]) : 4601;
    std::string outDir = (argc > 3) ? argv[3] : "hl7_messages";

    mkdir(outDir.c_str(), 0755);

    printf("HL7 ORU reader -> connecting to %s:%d\n", ip.c_str(), port);
    printf("Saving to ./%s/  (per-day log + latest.txt)\n\n", outDir.c_str());
    fflush(stdout);

    long seq = 1;
    while (true) {
        int fd = socket(AF_INET, SOCK_STREAM, 0);
        if (fd < 0) {
            perror("socket");
            sleep(2);
            continue;
        }
        struct sockaddr_in addr;
        memset(&addr, 0, sizeof(addr));
        addr.sin_family = AF_INET;
        addr.sin_port = htons(port);
        inet_pton(AF_INET, ip.c_str(), &addr.sin_addr);

        if (connect(fd, (struct sockaddr*)&addr, sizeof(addr)) == 0) {
            int one = 1;
            setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));
            setsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, &one, sizeof(one));
            printf(">> connected to monitor, streaming ORU...\n");
            fflush(stdout);
            pump(fd, outDir, seq);
            printf("<< monitor disconnected, reconnecting in 2s...\n\n");
            fflush(stdout);
        } else {
            printf("!! connect failed (%s), retry in 2s...\n", strerror(errno));
            fflush(stdout);
        }
        close(fd);
        sleep(2);
    }
    return 0;
}
