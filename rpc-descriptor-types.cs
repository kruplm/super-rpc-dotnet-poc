using System.Collections.Generic;
using System;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Collections;

namespace SuperRPC;

[JsonConverter(typeof(StringEnumConverter))]
public enum FunctionReturnBehavior {
    [EnumMember(Value = "void")]
    Void,
    [EnumMember(Value = "sync")]
    Sync,
    [EnumMember(Value = "async")]
    Async
}

public abstract record Descriptor(string type);

public record FunctionDescriptor(): Descriptor("function")
{
    [JsonProperty("name")]
    public string? Name;

    [JsonProperty("arguments", NullValueHandling = NullValueHandling.Ignore)]
    public ArgumentDescriptor[]? Arguments;

    [JsonProperty("returns", NullValueHandling = NullValueHandling.Ignore)]
    public FunctionReturnBehavior Returns = FunctionReturnBehavior.Async;

    public static implicit operator FunctionDescriptor(string name) => new FunctionDescriptor { Name = name };
}

public record PropertyDescriptor(): Descriptor("property")
{
    [JsonProperty("name")]
    public string? Name;

    [JsonProperty("get", NullValueHandling = NullValueHandling.Ignore)]
    public FunctionDescriptor? Get;

    [JsonProperty("set", NullValueHandling = NullValueHandling.Ignore)]
    public FunctionDescriptor? Set;

    [JsonProperty("readOnly", NullValueHandling = NullValueHandling.Ignore)]
    public bool? ReadOnly;

    public static implicit operator PropertyDescriptor(string name) => new PropertyDescriptor { Name = name };
}

public record ArgumentDescriptor : FunctionDescriptor 
{
    public int? idx;
}

public record ObjectDescriptor(): Descriptor("object")
{
    [JsonProperty("functions", NullValueHandling = NullValueHandling.Ignore)]
    public FunctionDescriptor[]? Functions;
    
    [JsonProperty("proxiedProperties", NullValueHandling = NullValueHandling.Ignore)]
    public PropertyDescriptor[]? ProxiedProperties;

    [JsonProperty("readonlyProperties", NullValueHandling = NullValueHandling.Ignore)]
    public string[]? ReadonlyProperties;
}

public record ObjectDescriptorWithProps: ObjectDescriptor {
    [JsonProperty("props")]
    public Dictionary<string, object> Props;

    public ObjectDescriptorWithProps(ObjectDescriptor other, Dictionary<string, object> props): base(other) {
        this.Props = props;
    }
}

public record ClassDescriptor(): Descriptor("class")
{
    [JsonProperty("classId")]
    public string? ClassId;

    [JsonProperty("ctor", NullValueHandling = NullValueHandling.Ignore)]
    public FunctionDescriptor? Ctor;

    [JsonProperty("static", NullValueHandling = NullValueHandling.Ignore)]
    public ObjectDescriptor? Static;

    [JsonProperty("instance", NullValueHandling = NullValueHandling.Ignore)]
    public ObjectDescriptor? Instance;
}
