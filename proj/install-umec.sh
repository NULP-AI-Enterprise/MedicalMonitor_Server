#!/usr/bin/env bash
# One-shot installer: makes the Mindray uMec10 HL7 collector survive reboots.
#   1) permanent static IP 192.168.0.99/24 on the USB-Ethernet adapter
#   2) systemd service that auto-starts decode.py --live on boot (auto-restart)
#
# Run once with:  sudo bash ~/proj/install-umec.sh
set -e

IFACE="enx00e04c68084b"     # the USB-Ethernet adapter the monitor is on
IPADDR="192.168.0.99/24"    # the address the monitor expects its server at

echo ">> [1/3] Creating permanent static-IP profile on $IFACE ..."
# let NetworkManager manage the adapter, then define a static profile for it
nmcli device set "$IFACE" managed yes || true
nmcli connection delete umec 2>/dev/null || true
nmcli connection add type ethernet ifname "$IFACE" con-name umec \
    ipv4.method manual ipv4.addresses "$IPADDR" \
    ipv4.never-default yes ipv6.method ignore \
    connection.autoconnect yes
nmcli connection up umec
echo "   static IP set:"
ip -br addr show "$IFACE"

echo ">> [2/3] Installing systemd service ..."
install -m 644 /home/hacaton1/proj/umec-hl7.service \
    /etc/systemd/system/umec-hl7.service
systemctl daemon-reload

echo ">> [3/3] Enabling + starting service ..."
# make sure no manual copy is holding the monitor's single connection
pkill -f 'decode.py --live' 2>/dev/null || true
sleep 1
systemctl enable --now umec-hl7.service
sleep 3
systemctl --no-pager --full status umec-hl7.service | head -12

echo
echo "DONE. It will now start automatically on every boot."
echo "  Results:   ~/proj/hl7_messages/decoded_latest.txt (+ .csv, trend, raw log)"
echo "  Status:    systemctl status umec-hl7"
echo "  Stop:      sudo systemctl stop umec-hl7      (frees the connection)"
echo "  Watch live: sudo systemctl stop umec-hl7 && python3 ~/proj/decode.py --live"
