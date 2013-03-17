﻿#region license
//Copyright (c) 2011-2013 <comparator@gmx.de>; Wassili Hense

//This file is part of the X13.Home project.
//https://github.com/X13home

//BSD License
//See LICENSE.txt file for license details.
#endregion license

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using X13.PLC;
using System.IO;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace X13.MQTT {
  public class MqBroker {
    #region static part
    private static TcpListener _tcp;
    private static Topic _mq;
    private static DVar<string> _admGroup;
    private static List<MqBroker> _connections;
    private static DVar<bool> _debug;

    public static void Open() {
      _mq=Topic.root.Get("/local/MQ");
      _tcp=new TcpListener(IPAddress.Any, 1883);
      _connections=new List<MqBroker>();
      _tcp.Start();
      _admGroup=Topic.root.Get<string>("/local/security/groups/0");
      _debug=Topic.root.Get<bool>("/system/log/MQTT");
      _tcp.BeginAcceptTcpClient(new AsyncCallback(Connect), null);
      Log.Info("Broker started on {0}", Environment.MachineName);
    }

    private static void Connect(IAsyncResult ar) {
      try {
        TcpClient c=_tcp.EndAcceptTcpClient(ar);
        _connections.Add(new MqBroker(c));
      }
      catch(ObjectDisposedException) {
        return;   // Socket allready closed
      }
      catch(SocketException) {
      }
      _tcp.BeginAcceptTcpClient(new AsyncCallback(Connect), null);
    }
    public static void Close() {
      foreach(var cl in _connections.ToArray()) {
        try {
          cl.Disconnect();
        }
        catch(Exception) {
        }
      }
      _tcp.Stop();

    }
    internal static bool CheckAuth(string user, string pass) {
      bool ret=false;
      Topic users=Topic.root.Get("/local/security/users");
      Topic pt;
      if(!user.Contains('/') && users.Exist(user, out pt)) {
        ret=((pt as DVar<string>).value==pass);
      }
      return ret;
    }
    internal static bool CheckAcl(string p, Topic t, TopicAcl topicAcl) {
      if(_admGroup.Exist(p)) {
        return true;              // Aministrators has all rights
      }
      while(t.grpOwner==null) {
        if(t==Topic.root) {
          return false;
        }
        t=t.parent;
      }
      if(t.grpOwner.Exist(p)) {
        return ((t.aclOwner & topicAcl)==topicAcl);
      } else {
        return ((t.aclAll & topicAcl)==topicAcl);
      }
    }
    #endregion static part

    #region instance part
    private Timer _tOut;
    private bool _connected=false;
    private MqConnect ConnInfo;
    private List<Topic.Subscription> _subscriptions=new List<Topic.Subscription>();
    private DVar<MqBroker> _owner;
    private MqStreamer _stream;

    private MqBroker(TcpClient cl) {
      _stream=new MqStreamer(cl, Received, null);
      //var re=((IPEndPoint)_stream.Socket.Client.RemoteEndPoint);
      //string name=re.Address.ToString()+"_"+re.Port.ToString("X4");
      _tOut=new Timer(new TimerCallback(TimeOut), null, 900, Timeout.Infinite);
    }

    private void OwnerChanged(Topic sender, TopicChanged param) {
      if(sender!=_mq && (sender.path.StartsWith("/local") || param.Visited(_mq, false) || param.Visited(_owner, true) || !CheckAcl(ConnInfo.userName, sender, TopicAcl.Subscribe))) {
        return;
      }
      MqPublish pm;
      pm=new MqPublish(sender);
      pm.QualityOfService=param.Subscription.qos;
      if(param.Art==TopicChanged.ChangeArt.Add && sender.valueType!=null && sender.valueType!=typeof(string) && !sender.valueType.IsEnum && !sender.valueType.IsPrimitive) {
        pm.Payload=(new Newtonsoft.Json.Linq.JObject(new Newtonsoft.Json.Linq.JProperty("+", sender.valueType.FullName))).ToString();
      } else if(param.Art==TopicChanged.ChangeArt.Remove) {
        pm.Payload=string.Empty;
      }
      this.Send(pm);
    }
    private void Received(MqMessage msg) {
      if(_debug) {
        Log.Debug("R {0} {1}", _owner==null?string.Empty:_owner.name, msg);
      }
      int toDelay=ConnInfo==null?600:(ConnInfo.keepAlive*1505);
      switch(msg.MsgType) {
      case MessageType.CONNECT:
        ConnInfo=msg as MqConnect;
        if(ConnInfo.userName!=null && !MqBroker.CheckAuth(ConnInfo.userName, ConnInfo.userPassword)) {
          _stream.Send(new MqConnack(MqConnack.MqttConnectionResponse.BadUsernameOrPassword));
          Log.Warning("BadUsernameOrPassword {0}:{1}@{2}", ConnInfo.userName, ConnInfo.userPassword, ConnInfo.clientId);
          _stream.Send(new MqDisconnect());
          break;
        }
        _stream.Send(new MqConnack(MqConnack.MqttConnectionResponse.Accepted));
        _connected=true;
        _owner=_mq.Get<MqBroker>(ConnInfo.clientId);
        _owner.value=this;

        if(ConnInfo.keepAlive!=0) {
          _stream.Socket.ReceiveTimeout=0;
          toDelay=ConnInfo.keepAlive*1505;
        }
        Log.Info("{0} Connected", ConnInfo.clientId);
        break;
      case MessageType.DISCONNECT:
        Disconnect();
        break;
      case MessageType.PINGREQ:
        this.Send(new MqPingResp());
        break;
      case MessageType.SUBSCRIBE: {
          MqSubscribe subMsg=msg as MqSubscribe;
          MqSuback ackMsg=new MqSuback();
          ackMsg.MessageID=subMsg.MessageID;
          foreach(var it in subMsg.list) {
            if(it.Key.StartsWith("/local")) {
              ackMsg.Add(QoS.AtMostOnce);     // not allowed
              Log.Warning("{0} not allowed subscription: {1}", _owner.path, it.Key);
            } else {
              var s=_owner.Subscribe(it.Key, OwnerChanged, it.Value);
              _subscriptions.Add(s);
              ackMsg.Add(s.qos);
            }
          }
          this.Send(ackMsg);
        }
        break;
      case MessageType.UNSUBSCRIBE: {
          MqUnsubscribe subMsg=msg as MqUnsubscribe;
          MqUnsuback ackMsg=new MqUnsuback();
          ackMsg.MessageID=subMsg.MessageID;
          foreach(var it in subMsg.list) {
            int id=_subscriptions.FindIndex(el => el.path==it);
            if(id>=0) {
              _owner.Unsubscribe(_subscriptions[id].path, OwnerChanged);
              _subscriptions.RemoveAt(id);
            }
          }
          this.Send(ackMsg);
        }
        break;
      case MessageType.PUBLISH: {
          MqPublish pm=msg as MqPublish;
          if(msg.MessageID!=0) {
            if(msg.QualityOfService==QoS.AtLeastOnce) {
              this.Send(new MqMsgAck(MessageType.PUBACK, msg.MessageID));
            } else if(msg.QualityOfService==QoS.ExactlyOnce) {
              this.Send(new MqMsgAck(MessageType.PUBREC, msg.MessageID));
            }
          }
          ProccessPublishMsg(pm);
        }
        break;
      case MessageType.PUBACK:
        break;
      case MessageType.PUBREC:
        if(msg.MessageID!=0) {
          this.Send(new MqMsgAck(MessageType.PUBREL, msg.MessageID));
        }
        break;
      case MessageType.PUBREL:
        if(msg.MessageID!=0) {
          this.Send(new MqMsgAck(MessageType.PUBCOMP, msg.MessageID));
        }
        break;
      case MessageType.PUBCOMP:
        break;
      default:
        break;
      }
      if(_connected && toDelay>0) {
        _tOut.Change(toDelay, Timeout.Infinite);
      } else {
        _tOut.Change(Timeout.Infinite, Timeout.Infinite);
      }
    }

    private void ProccessPublishMsg(MqPublish pm) {
      Topic cur=Topic.root;
      Type vt=null;

      string[] pt=pm.Path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
      if(pt.Length>0 && pt[0]=="local") {
        return;
      }
      int i=0;
      while(i<pt.Length && cur.Exist(pt[i])) {
        cur=cur.Get(pt[i++]);
      }
      if(string.IsNullOrEmpty(pm.Payload)) {                      // Remove
        if(i==pt.Length && CheckAcl(ConnInfo.userName, cur, TopicAcl.Delete)) {
          cur.Remove(_owner);
        }
        return;
      }
      if(i<pt.Length || cur.valueType==null) {                             // pm.Path not exist
        vt=X13.WOUM.ExConverter.Json2Type(pm.Payload);
        if(!CheckAcl(ConnInfo.userName, cur, TopicAcl.Create)) {
          return;
        }
        cur=Topic.GetP(pm.Path, vt, _owner);        // Create
      }

      if(cur.valueType!=null) {                 // Publish
        if(CheckAcl(ConnInfo.userName, cur, TopicAcl.Change)) {
          cur.saved=pm.Retained;
          cur.FromJson(pm.Payload, _owner);
        }
      }
    }
    private void TimeOut(object o) {
      Disconnect();
      Log.Warning("Timeout by {0}", _owner!=null?_owner.name:"unknown");
    }
    private void Send(MqMessage msg) {
      if(_stream!=null) {
        _stream.Send(msg);
        if(_debug) {
          Log.Debug("S {0} {1}", _owner==null?string.Empty:_owner.name, msg);
        }
      }
    }

    private void Disconnect() {
      if(_connected && _stream!=null) {
        _connected=false;
        if(_stream!=null) {
          _stream.Send(new MqDisconnect());
          Thread.Sleep(0);
          _stream.Close();
          _stream=null;
        }
        Log.Info("{0} Disconnected", _owner.name);
      }
      if(_owner!=null) {
        foreach(var s in _subscriptions) {
          _owner.Unsubscribe(s.path, OwnerChanged);
        }
        _subscriptions.Clear();
        _owner.Remove();
      }
      if(_connections!=null) {
        _connections.Remove(this);
      }
    }
    #endregion instance part
  }
}
