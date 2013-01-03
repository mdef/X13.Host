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
using System.ComponentModel;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Xml.Linq;

namespace X13.PLC {
  /// <summary> Basis class in Message Queue Telemetry Library</summary>
  public partial class Topic : IComparable<Topic> {
    private static char[] _delmiter=new char[] { '/' };
    protected static readonly Topic _mq;
    private static WOUM.BlockingQueue<TopicChanged> _publishQueue;

    static Topic() {
      root=new Topic(null);
      root.path="/";
      _publishQueue=new WOUM.BlockingQueue<TopicChanged>(PubAction);
      _mq=root.Get("/local/MQ");
      _mq._childNodes=new SortedList<string, Topic>(1);
    }
    internal static bool paused { get { return _publishQueue.paused; } set { _publishQueue.paused=value; } }
    /// <summary>Find or create Topic</summary>
    /// <param name="path"></param>
    /// <param name="pt">null for Topic or Type for DVar of type pt</param>
    /// <param name="initiator"></param>
    /// <param name="root">Start topic for relative path</param>
    /// <returns></returns>
    internal static Topic GetP(string path, Type pt, Topic initiator=null, Topic home=null) {
      Topic cur;
      Topic next=null;
      if(home==null || (!string.IsNullOrEmpty(path) && path.StartsWith("/"))) {
        cur=root;
      } else {
        cur=home;
      }
      if(string.IsNullOrEmpty(path)) {
        return cur;
      }
      Type type=pt==null?typeof(Topic):typeof(DVar<>).MakeGenericType(pt);
      string[] pe=path.Split(_delmiter, StringSplitOptions.RemoveEmptyEntries);
      for(int i=0; i<pe.Length; i++, cur=next) {
        if(cur._childNodes==null) {
          cur._childNodes=new SortedList<string, Topic>();
        }
        bool chExist=cur._childNodes.TryGetValue(pe[i], out next);
        if(!chExist) {
          lock(cur) {
            chExist=cur._childNodes.TryGetValue(pe[i], out next);
            if(!chExist) {
              if(pe[i]=="+" || pe[i]=="#") {
                throw new ArgumentException("path ("+path+") is not valid");
              }
              if(i<pe.Length-1 || type==typeof(Topic)) {
                next=new Topic(null);
              } else {
                next = (Topic)Activator.CreateInstance(type);
              }
              next.parent=cur;
              next._name=pe[i];
              if(cur!=root) {
                next.path=cur.path+"/"+pe[i];
              } else {
                next.path="/"+pe[i];
              }
              cur._childNodes.Add(pe[i], next);
            }
          }
        }
        if(chExist) {
          if(i==pe.Length-1 && type!=typeof(Topic) && !type.IsAssignableFrom(next.GetType())) {
            Topic tmp= (Topic)Activator.CreateInstance(type);
            tmp.CopyFrom(next);
            next=tmp;
          }
        } else {
          if(i==pe.Length-1 && initiator!=root) {
            next.Publish(next, TopicChanged.ChangeArt.Add, initiator);
          }
        }
      }
      return cur;
    }
    private static void PubAction(TopicChanged tc) {
      if(tc.Task!=null){
        tc.Source=tc.Task.Current;
        tc.Sender.PublishSubs(tc, tc.Subscription.func);
        if(tc.Task.MoveNext()) {
          _publishQueue.Enqueue(new TopicChanged(tc));
        }
      } else {
        while(tc.Sender!=null) {
          tc.Sender.onChange(tc.Sender, tc);
          tc.Sender=tc.Sender.parent;
        }
      }
    }
    private static void Import(XElement xElement, Topic owner) {
      if(xElement==null || owner==null) {
        return;
      }
      if(xElement.Attribute("name")!=null) {
        Type tp;
        if(xElement.Attribute("type")!=null) {
          tp=Type.GetType(xElement.Attribute("type").Value);
        } else {
          tp=null;
        }
        Topic cur=GetP(xElement.Attribute("name").Value, tp, null, owner);
        foreach(var xNext in xElement.Elements("item")) {
          Import(xNext, cur);
        }
        cur.saved=xElement.Attribute("saved")!=null && xElement.Attribute("saved").Value==bool.TrueString;
        if(tp!=null && xElement.Attribute("value")!=null) {
          cur.FromJson(xElement.Attribute("value").Value);
        }
      }
    }
    private static void Export(XElement xParent, Topic tCur) {
      if(xParent==null || tCur==null) {
        return;
      }
      XElement xCur=new XElement("item", new XAttribute("name", tCur.name));
      ;
      if(tCur.valueType!=null) {
        xCur.Add(new XAttribute("value", tCur.ToJson()));
        if(tCur.saved) {
          xCur.Add(new XAttribute("saved", bool.TrueString));
        }
        xCur.Add(new XAttribute("type", tCur.valueType.FullName));
      }
      xParent.Add(xCur);
      foreach(Topic tNext in tCur.children) {
        Export(xCur, tNext);
      }
    }

    private string _name;
    protected string _json;
    protected int _disposed=0;
    protected SortedList<string, Topic> _childNodes=null;
    private List<Subscription> _subs=new List<Subscription>();

    internal TopicAcl aclAll;
    internal TopicAcl aclOwner;
    internal Topic grpOwner;

    protected Topic(Type t) {
      valueType=t;
    }

    internal string ToJson() {
      if(_json==null && valueType!=null) {
        lock(_name) {
          if(_json==null) {
            if(valueType==typeof(Topic)) {
              Topic link=this.GetValue() as Topic;
              if(link==null) {
                _json="{ }";
              } else {
                string sPath=link.path;
                Stack<Topic> mPath=new Stack<Topic>();
                Topic cur=this;
                do {
                  mPath.Push(cur);
                } while((cur=cur.parent)!=root);
                Stack<Topic> lPath=new Stack<Topic>();
                cur=link;
                do {
                  lPath.Push(cur);
                } while((cur=cur.parent)!=root);
                while(mPath.Peek()==lPath.Peek()) {
                  mPath.Pop();
                  lPath.Pop();
                }
                if(mPath.Count<4) {
                  StringBuilder sb=new StringBuilder();
                  for(int i=mPath.Count-1; i>=0; i--) {
                    sb.Append("../");
                  }
                  while(lPath.Count>0) {
                    if(lPath.Count>1) {
                      sb.AppendFormat("{0}/", lPath.Pop().name);
                    } else {
                      sb.AppendFormat(lPath.Pop().name);
                    }
                  }
                  sPath=sb.ToString();
                }
                _json=(new JObject(
                  new JProperty("p", sPath),
                  new JProperty("t", link.valueType==null?string.Empty:link.valueType.FullName))).ToString();
              }
            } else {
              _json=Newtonsoft.Json.JsonConvert.SerializeObject(GetValue());
            }
          }
        }
      }
      return _json;
    }
    internal void FromJson(string json, Topic initiator=null) {
      if(valueType==null || string.IsNullOrEmpty(json)) {
        return;   // do nothing
      }
      TopicChanged param=new TopicChanged(TopicChanged.ChangeArt.Value) { Source=this };
      if(initiator!=null) {
        param.Visited(initiator, true);
      }
      if(valueType==typeof(Topic)) {
        var jo=JObject.Parse(json);
        string t1=(string)jo["p"];
        string t2=(string)jo["t"];
        Type tt;
        if(string.IsNullOrEmpty(t2)) {
          tt=null;
        } else {
          tt=Type.GetType(t2);
        }
        if(t1.StartsWith("../")) {
          Topic mop=this;
          while(t1.StartsWith("../")) {
            t1=t1.Substring(3);
            mop=mop.parent;
          }
          t1=mop.path+"/"+t1;
        }
        Topic tc=Topic.GetP(t1, tt, initiator);
        this.SetValue(tc, param);
      } else if(valueType!=null) {
        this.SetValue(JsonConvert.DeserializeObject(json, valueType), param);
      }

    }
    private void UpdateMovedTopicsDeep() {
      if(this.parent!=root) {
        this.path=this.parent.path+"/"+this.name;
      } else {
        this.path="/"+this.name;
      }
      this.Publish(this, TopicChanged.ChangeArt.Add, null);
      if(this._childNodes!=null) {
        foreach(Topic next in _childNodes.Values) {
          next.UpdateMovedTopicsDeep();
        }
      }
      this.Publish(this, TopicChanged.ChangeArt.Value, null);
    }
    protected void CopyFrom(Topic old) {
      if(old.valueType!=null) {
        throw new ArgumentException();
      }
      this.parent=old.parent;
      this._childNodes=old._childNodes;
      lock(old._subs) {
        this._subs=old._subs;
      }
      this._name=old.name;
      this.path=old.path;
      if(this._childNodes!=null) {
        foreach(var ch in _childNodes) {
          ch.Value.parent=this;
        }
      }
      this.parent._childNodes.Remove(old.name);
      this.parent._childNodes.Add(this.name, this);
      old.parent=null;
    }

    internal void Publish(Topic sender, TopicChanged.ChangeArt art, Topic initiator=null) {
      Publish(sender, new TopicChanged(art, initiator) { Source=sender });
    }
    protected void DVar_changed(Topic sender, TopicChanged args) {
      Publish(sender, args.Art, null);
    }
    protected void Publish(Topic sender, TopicChanged tc) {
      if(_disposed>1) {
        throw new ObjectDisposedException(this.path);
      }
      tc.Sender=this;
      _publishQueue.Enqueue(tc);
    }
    protected virtual void onChange(Topic sender, TopicChanged param) {
      if(_disposed>1 && param.Art!=TopicChanged.ChangeArt.Remove && param.Source!=this)
        return;
      {
        Subscription[] sa;
        lock(this._subs) {
          sa=this._subs.ToArray();
        }
        foreach(var s in sa) {
          bool passt=false;
          //Check subscribtion
          if(param.Source==this) {
            if(s.lvls.Length==0 || s.lvls[0]=="#") {
              passt=true;
            }
          } else {
            Topic cur=param.Source;
            Stack<Topic> rp=new Stack<Topic>();
            do {
              rp.Push(cur);
              cur=cur.parent;
              if(cur==null) {
                break;
              }
            } while(cur!=this);
            if(cur!=this) {   // is not child from this
              continue;
            }
            for(int i=0; i<s.lvls.Length; i++) {
              if(rp.Count==0) {
                passt=false;
                break;
              }
              cur=rp.Pop();
              if(s.lvls[i]=="#") {
                passt=true;
                break;
              }
              if(s.lvls[i]!="+" && s.lvls[i]!=cur.name && (rp.Count!=0 || i+1!=s.lvls.Length)) {
                passt=false;
                break;
              }
              passt=rp.Count==0;
            }
          }
          if(passt) {
            param.Subscription=s;
            PublishSubs(param, s.func);
          }
        }
      }
    }
    private void PublishSubs(TopicChanged param, Action<Topic, TopicChanged> func) {
      Topic link;
      try {
        if((link=func.Target as Topic)!=null) {
          //if(!param.Visited(param.Source, true)) {
          link.onChange(this, param);
          //}
        } else {
          func.Invoke(param.Source, param);
        }
      }
      catch(Exception ex) {
        if(ex.InnerException!=null) {
          ex=ex.InnerException;
        }
        Log.Error("Exception in {0}.{1}({2}, {3}) - {4}", func.Target, func.Method.Name, this.ToString(), param.Art.ToString(), ex.ToString());
      }
    }

    private class TopicEnumerator : IEnumerable<Topic> {
      private static string[] _all=new string[] { "#" };
      private static string[] _children=new string[] { "+" };

      private readonly string[] _levels;
      private int _lvl;
      private Topic _cur;

      public TopicEnumerator(Topic t, bool deep) {
        _cur=t;
        _lvl=0;
        _levels=deep?_all:_children;
      }
      public TopicEnumerator(string[] levels, int lvl, Topic cur) {
        _levels=levels;
        _lvl=lvl;
        _cur=cur;
      }
      public IEnumerator<Topic> GetEnumerator() {
        if(_lvl==_levels.Length) {
          yield return _cur;
        }
        for(; _lvl<_levels.Length; _lvl++) {
          if(_cur==null) {
            break;
          }
          if(_levels[_lvl]=="#") {
            yield return _cur;
            if(_cur._childNodes!=null) {
              foreach(Topic ts in _cur._childNodes.Values.ToArray()) {
                if(_cur==root && ts.name=="local") {
                  continue;
                }
                foreach(Topic t2 in new TopicEnumerator(_levels, _lvl, ts)) {
                  yield return t2;
                }
              }
            }
          } else if(_levels[_lvl]=="+") {
            if(_cur._childNodes!=null) {
              foreach(Topic ts in _cur._childNodes.Values.ToArray()) {
                if(_lvl<_levels.Length-1) {
                  foreach(Topic t2 in new TopicEnumerator(_levels, _lvl+1, ts)) {
                    yield return t2;
                  }
                } else {
                  yield return ts;
                }
              }
            }
            break;
          } else {
            if(_cur._childNodes!=null) {
              if(_cur._childNodes.TryGetValue(_levels[_lvl], out _cur)) {
                if(_lvl==_levels.Length-1) {
                  yield return _cur;
                  break;
                }
                continue;
              }
              break;
            }
          }
        }
      }

      System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() {
        return GetEnumerator();
      }
    }
  }
  [Flags]
  internal enum TopicAcl {
    None=0,
    Subscribe=1,
    Change=2,
    Create=4,
    Delete=8,
    Full=15
  }
}