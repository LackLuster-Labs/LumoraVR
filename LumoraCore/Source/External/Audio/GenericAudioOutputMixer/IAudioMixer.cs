using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
namespace Lumora.Core.External.GenericAudioOutputMixer;

public interface IAudioMixer
{
    ///<summary>
    ///Create An IAudioBus With the name <paramref name="name"/>
    ///the <see langword="return"/> value is <see langword="true"/> if successful and <see langword="false"/> if the bus exists
    ///the <paramref name="bus"/> paramerter is the created bus only popylated if the returniong true
    ///</summary>
    public bool CreateAudioBus(string name, [NotNullWhen(true)] out IAudioBus? bus);
    ///<summary>
    ///Get The IAudioBus with the name <paramref name="name"/> or <see langword="throw"/>
    ///NOT RECOMENDED
    ///</summary>
    public IAudioBus GetAudioBusByName(string name);
    ///<summary>
    ///Is not null if audio bus exists
    ///<paramref name="name"/> is the name of the bus you want to get
    ///</summary>
    public IAudioBus? GetAudioBusByNameOrNull(string name);
    ///<summary>
    ///The Try variant of <see cref="GetAudioBusByNameOrNull(string)"/> the <paramref name="bus"/> is populated if it was successful 
    ///</summary>
    public bool TryGetAudioBusByName(string name, [NotNullWhen(true)] out IAudioBus? bus);
    ///<summary>
    ///Get all active buses.
    ///</summary>
    public IAudioBus[] GetAllBuses();
    public string[] GetAvailableAudioEffects();
    public void ForceSync();
}
