﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Linq;
using SignalGo.Shared.Helpers;

namespace SignalGo.Shared.DataTypes
{
    /// <summary>
    /// system custom data exchanger help you to ignore or take custom properties to serialize data
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true)]
    public class CustomDataExchangerAttribute : Attribute
    {
        public CustomDataExchangerAttribute()
        {

        }
        /// <summary>
        /// default constructor for data exchanger
        /// </summary>
        /// <param name="type">type of your class to ignore or take properties for serialize</param>
        /// <param name="properties">property names that you need to ignore or take for serialize</param>
        public CustomDataExchangerAttribute(Type type, params string[] properties)
        {
            Type = type;
            Properties = properties;
        }

        /// <summary>
        /// default constructor for data exchanger
        /// this is use for class types not methods
        /// </summary>
        /// <param name="properties">property names that you need to ignore or take for serialize</param>
        public CustomDataExchangerAttribute(params string[] properties)
        {
            Properties = properties;
        }

        public CustomDataExchangerAttribute(LimitExchangeType limitExchangeType)
        {
            LimitationMode = limitExchangeType;
        }


        /// <summary>
        /// default constructor for data exchanger
        /// if properties was null system take or ignore full properties and not ignoring
        /// </summary>
        /// <param name="type">type of your class to ignore or take or ignore properties for serialize</param>
        public CustomDataExchangerAttribute(Type type)
        {
            Type = type;
        }

        /// <summary>
        /// default constructor for data exchanger
        /// </summary>
        /// <param name="type">type of your class to ignore or take or ignore properties for serialize</param>
        /// <param name="properties">list of types you want to take or ignore methods of that types</param>
        public CustomDataExchangerAttribute(Type type, params Type[] properties)
        {
            Type = type;
            Properties = GetProperties(properties).ToArray();
        }

        /// <summary>
        /// default constructor for data exchanger
        /// this is use for class types not methods
        /// </summary>
        /// <param name="properties">list of types you want to take or ignore methods of that types</param>
        public CustomDataExchangerAttribute(params Type[] properties)
        {
            Properties = GetProperties(properties).ToArray();
        }
        /// <summary>
        /// type of data exchanger you need to ignore that peroperties or take
        /// </summary>
        public CustomDataExchangerType ExchangeType { get; set; } = CustomDataExchangerType.Take;
        /// <summary>
        /// limitation mode in incoming call or outgoingCall or both
        /// </summary>
        public LimitExchangeType LimitationMode { get; set; } = LimitExchangeType.Both;
        /// <summary>
        /// type of your class to ignore or take properties for serialize
        /// </summary>
        public Type Type { get; set; }
        /// <summary>
        /// inverse LimitationMode for client side
        /// </summary>
        public bool InverseLimitationForClientSide { get; set; } = true;
        /// <summary>
        /// property names that you need to ignore or take for serialize
        /// </summary>
        public string[] Properties { get; set; } = null;

        public bool ContainsProperty(string name)
        {
            if (Properties == null)
                return true;
            return Properties.Contains(name);
        }

       
        /// <summary>
        /// get list of methods of type
        /// </summary>
        /// <param name="types">your types</param>
        /// <returns>list of methods names</returns>
        public static List<string> GetProperties(Type[] types)
        {
            List<string> result = new List<string>();
            foreach (var serviceType in types)
            {
                foreach (var item in serviceType.GetListOfInterfaces())
                {
                    result.AddRange(item.GetListOfProperties().Select(x => x.Name));
                }

                var parent = serviceType.GetBaseType();

                while (parent != null)
                {
                    result.AddRange(parent.GetListOfProperties().Select(x => x.Name));

                    foreach (var item in parent.GetListOfInterfaces())
                    {
                        result.AddRange(item.GetListOfProperties().Select(x => x.Name));
                    }
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                    parent = parent.GetTypeInfo().BaseType;
#else
                    parent = parent.GetBaseType();
#endif
                }
            }
            return result;
        }

        /// <summary>
        /// you can customize enable and disable ignorable
        /// </summary>
        /// <returns>if you return false system force skip to ignore property</returns>
        //public virtual bool IsEnabled(object client, object server, string propertyName, Type type)
        //{
        //    return true;
        //}

        /// <summary>
        /// you can create your custom skipper with override this method
        /// when you want to skip to serialize or deserialize your object return true else return false
        /// default value is null, if you want to system use LimitExchangeType for ignore or not ignore object return null
        /// </summary>
        /// <param name="model">object model that want to serialize or deserialize</param>
        /// <param name="property">property of type that want serialize or deserialize,if it is null parameter type is fill</param>
        /// <param name="fieldInfo"></param>
        /// <param name="type">type that want serialize or deserialize</param>
        /// <param name="client"></param>
        /// <param name="server"></param>
        /// <param name="attribute">attribute</param>
        /// <returns></returns>
        public virtual bool? CanIgnore(object model, PropertyInfo property, FieldInfo fieldInfo, Type type, object client, object server)
        {
            return null;
        }

        /// <summary>
        /// get data exchenger by user
        /// </summary>
        /// <param name="client"></param>
        /// <returns>if you return false this attibute will be ignored</returns>
        public virtual bool GetExchangerByUserCustomization(object client)
        {
            return true;
        }
    }
}
