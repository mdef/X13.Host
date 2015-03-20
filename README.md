#Fork of X13.Host
intended to be more stable on Linux systems

Features added
* Disabled automatic serial port scan. Port must be configured in port.txt file
* improved detection of USB Serial Arduino nano (atmega328) based gate.

Currently tested on 
* Cubieboard  mono version 3.2.8
	http://www.igorpecovnik.com/2013/12/24/cubietruck-debian-wheezy-sd-card-image/
	name of image Cubietruck_Debian_2.9_jessie_3.4.105.zip
* Ubuntu 14.04 mono version 3.10.1


Running
```bash
cd /opt/ && git clone -b linux-bin https://github.com/mdef/X13.Host
cd /opt/X13.Host/bin; mono engine.exe
```
Note: edit file /opt/X13.Host/bin/port.txt if device not in port /dev/ttyUSB0 





#Overall project description

**Server side**

Normally you have Linux/Windows box where installed X13.Host engine.exe
and inserted Arduino nano (atmega328) based device with [firmware](https://github.com/mdef/X13.devices/tree/nano-bin/devices/A1SC12 "") for simplicity named as gate

**Remote devices (nodes) side**

Arduino nano (atmega328) nodes with [firmware](https://github.com/mdef/X13.devices/tree/nano-bin/devices/A1Cn12 "") wirelessly connected to gate.


**Client side**

Windows box to run X13.Host [cc.exe](https://github.com/X13home/x13home.github.io/tree/bin_main/bin "") - this application gives you tree view
of all x13 devices, nodes configuration and PLC editor. CC.exe is only client, all settings stored on server side.
Client have to be configured with hostname/ip address, username, password.

Username and password located on server side in directory /opt/X13.Host/data  file security.xst

For further integration the same credentials could be used to access X13.Host from any MQTT subscribe client for example [Node-Red](http://nodered.org/ "")

