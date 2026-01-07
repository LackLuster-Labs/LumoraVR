using System;
using Godot;
using System.Collections.Generic;
using Lumora.Core.External.GenericAudioOutputMixer;
namespace Aquamarine.Source.Godot;

public partial class AudioBus : IAudioBus
{
    private int _busId;
    private bool _isValid = false;
    internal bool IsValid { get => _isValid; }
    internal void Invalidate()
    {
        _isValid = false;
    }
    public void SetVolumeDB(float volume)
    {
        if (!_isValid)
            throw new Exception("This Bus Refrence is no longer valid");
        AudioServer.SetBusVolumeDb(_busId, volume);
    }
    public void SetVolumeNormalized(float volume)
    {
        if (!_isValid)
            throw new Exception("This Bus Refrence is no longer valid");
        AudioServer.SetBusVolumeLinear(_busId, volume);
    }
    public float GetVolumeDB()
    {
        if (!_isValid)
            throw new Exception("This Bus Refrence is no longer valid");
        return AudioServer.GetBusVolumeDb(_busId);
    }
    public float GetVolumeNormalized()
    {
        if (!_isValid)
            throw new Exception("This Bus Refrence is no longer valid");
        return AudioServer.GetBusVolumeLinear(_busId);
    }
    public string Name
    {
        get
        {
            if (!_isValid)
                throw new Exception("This Bus Refrence is no longer valid");
            return AudioServer.GetBusName(_busId);
        }
        set
        {
            if (!_isValid)
                throw new Exception("This Bus Refrence is no longer valid");
            AudioServer.SetBusName(_busId, value);
        }
    }
    public List<IAudioEffect> Effects
    {
        get { throw new NotImplementedException(); }
    }
    public IAudioBus Target
    {
        get
        {
            if (!_isValid)
                throw new Exception("This Bus Refrence is no longer valid");
            return AudioMixer.GetMixer().GetAudioBusByName(AudioServer.GetBusSend(_busId));
        }
        set
        {
            if (!_isValid)
                throw new Exception("This Bus Refrence is no longer valid");
            AudioServer.SetBusSend(_busId, value.Name);
        }
    }
    internal int BusId
    {
        get => _busId;
    }
    internal AudioBus(int busid)
    {
        Lumora.Core.Logging.Logger.Log("creating bus");
        _isValid = true;
        _busId = busid;
    }
    public float Channels { get => 2; }
}

