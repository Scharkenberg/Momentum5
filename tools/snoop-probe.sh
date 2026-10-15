#!/system/bin/sh
echo "== props"; getprop | grep -i -E "snoop|btsnoop|hci_log"
echo "== settings"; settings list global | grep -i -E "snoop|hci"; settings list secure | grep -i -E "snoop|hci"
echo "== dirs"; ls -laZ /data/misc/bluetooth /data/misc/bluetooth/logs /data/misc/bluedroid /data/log/bt 2>&1 | head -40
echo "== find"; find /data /sdcard /storage -iname "*snoop*" 2>/dev/null | head -20
echo "== bt proc"; ps -A | grep -i -E "bluetooth|btservice" | head
echo "== apex"; ls /apex | grep -i bluetooth
echo "== selinux"; getenforce
