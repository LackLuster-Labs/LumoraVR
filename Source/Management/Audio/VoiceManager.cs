using System;
using System.Collections.Concurrent;
using Godot;
using LiteNetLib;
using LiteNetLib.Utils;
using NAudio.Wave;
using System.Collections.Generic;
using Aquamarine.Source.Logging;
using Aquamarine.Source.Management;

namespace Aquamarine.Source.Networking
{
    public partial class VoiceManager : Node
    {
        private const int SAMPLE_RATE = 48000;
        private const int CHANNELS = 1;
        private const int BITS_PER_SAMPLE = 16;
        private const byte VOICE_CHANNEL = 63; // Use second-to-last channel for voice
        private const float MAX_VOICE_DISTANCE = 20.0f; // Maximum distance for voice audio in 3D space

        private WaveInEvent _waveIn;
        private readonly ConcurrentDictionary<int, AudioStreamPlayer3D> _voicePlayers = new();
        private readonly ConcurrentQueue<(int senderId, byte[] audioData, Vector3 position)> _audioQueue = new();
        private readonly byte[] _recordBuffer = new byte[4096];
        private LiteNetLibMultiplayerPeer _peer;
        private MultiplayerScene _multiplayerScene;
        private float _currentInputLevel = 0f;
        private const float INPUT_LEVEL_DECAY = 0.1f;

        public void Initialize(LiteNetLibMultiplayerPeer peer, MultiplayerScene multiplayerScene)
        {
            _peer = peer;
            _multiplayerScene = multiplayerScene;

            // Setup voice capture
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SAMPLE_RATE, BITS_PER_SAMPLE, CHANNELS),
                BufferMilliseconds = 50
            };

            _waveIn.DataAvailable += WaveInOnDataAvailable;

            // Register network events
            if (_peer != null)
            {
                _peer.Listener.NetworkReceiveEvent += OnVoiceDataReceived;
                _peer.PeerConnected += OnPeerConnected;
                _peer.PeerDisconnected += OnPeerDisconnected;
            }

            Logger.Log("Voice Manager initialized");
        }

        // Custom serialization for Vector3
        private void WriteVector3(NetDataWriter writer, Vector3 vector)
        {
            writer.Put(vector.X);
            writer.Put(vector.Y);
            writer.Put(vector.Z);
        }

        private Vector3 ReadVector3(NetPacketReader reader)
        {
            return new Vector3(
                reader.GetFloat(),
                reader.GetFloat(),
                reader.GetFloat()
            );
        }

        public void StartVoiceCapture()
        {
            try
            {
                _waveIn.StartRecording();
                Logger.Log("Started voice capture");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"Failed to start voice capture: {ex.Message}");
            }
        }

        public void StopVoiceCapture()
        {
            try
            {
                _waveIn.StopRecording();
                Logger.Log("Stopped voice capture");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"Failed to stop voice capture: {ex.Message}");
            }
        }

        private void WaveInOnDataAvailable(object sender, WaveInEventArgs e)
        {
            UpdateInputLevel(e.Buffer);

            if (_peer == null || !IsInstanceValid(_peer)) return;
            if (!_peer._IsServer() && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected)
            {
                // Get local player position for 3D audio
                var localPlayer = _multiplayerScene?.GetLocalPlayer();
                if (localPlayer == null) return;

                var writer = new NetDataWriter();
                WriteVector3(writer, localPlayer.GlobalPosition);
                writer.Put(e.Buffer);

                // Send voice data using the network manager
                Logger.Debug($"Sending voice data to server");
                if (_peer.NetManager.ConnectedPeerList.Count > 0)
                {
                    _peer.NetManager.FirstPeer?.Send(writer, VOICE_CHANNEL, DeliveryMethod.Unreliable);
                }
            }
        }

        private void OnVoiceDataReceived(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            if (channel != VOICE_CHANNEL) return;

            try
            {
                var senderPosition = ReadVector3(reader);
                var audioData = reader.GetRemainingBytes();

                if (_peer._IsServer())
                {
                    // Server relays voice data to other clients
                    foreach (var otherPeer in _peer.NetManager.ConnectedPeerList)
                    {
                        if (otherPeer != peer)
                        {
                            var relayWriter = new NetDataWriter();
                            WriteVector3(relayWriter, senderPosition);
                            relayWriter.Put(audioData);
                            otherPeer.Send(relayWriter, VOICE_CHANNEL, DeliveryMethod.Unreliable);
                        }
                    }
                }
                else
                {
                    // Client processes received voice data
                    var senderId = Multiplayer.GetRemoteSenderId();
                    _audioQueue.Enqueue((senderId, audioData, senderPosition));
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"Error processing voice data: {ex.Message}");
            }
        }

        public override void _Process(double delta)
        {
            while (_audioQueue.TryDequeue(out var voiceData))
            {
                ProcessVoiceData(voiceData.senderId, voiceData.audioData, voiceData.position);
            }

            // Decay input level over time
            _currentInputLevel = Mathf.MoveToward(_currentInputLevel, 0f, (float)delta * INPUT_LEVEL_DECAY);
        }

        private void ProcessVoiceData(int senderId, byte[] audioData, Vector3 speakerPosition)
        {
            if (!_voicePlayers.TryGetValue(senderId, out var player))
            {
                player = CreateVoicePlayer();
                Logger.Log($"Creating voice player for {senderId} with id {player.Name}");
                _voicePlayers[senderId] = player;
            }

            // Update 3D position
            player.GlobalPosition = speakerPosition;

            // Calculate attenuation based on distance to local player
            var localPlayer = _multiplayerScene?.GetLocalPlayer();
            if (localPlayer != null)
            {
                var distance = localPlayer.GlobalPosition.DistanceTo(speakerPosition);
                var attenuation = Mathf.Clamp(1.0f - (distance / MAX_VOICE_DISTANCE), 0.0f, 1.0f);
                player.VolumeDb = Mathf.LinearToDb(attenuation);
            }

            // Create and play audio stream
            var audioStream = new AudioStreamWav();
            audioStream.Data = audioData;
            audioStream.Format = AudioStreamWav.FormatEnum.Format16Bits;
            audioStream.MixRate = SAMPLE_RATE;
            audioStream.Stereo = false;

            player.Stream = audioStream;
            player.Play();
        }

        private AudioStreamPlayer3D CreateVoicePlayer()
        {
            var player = new AudioStreamPlayer3D
            {
                MaxDistance = MAX_VOICE_DISTANCE,
                UnitSize = 1.0f,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            };
            AddChild(player);
            return player;
        }

        private void OnPeerConnected(long peerId)
        {
            if (!_voicePlayers.ContainsKey((int)peerId))
            {
                _voicePlayers[(int)peerId] = CreateVoicePlayer();
            }
        }

        private void OnPeerDisconnected(long peerId)
        {
            if (_voicePlayers.TryRemove((int)peerId, out var player))
            {
                player.QueueFree();
            }
        }

        private void UpdateInputLevel(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                _currentInputLevel = Mathf.MoveToward(_currentInputLevel, 0f, INPUT_LEVEL_DECAY);
                return;
            }

            float sum = 0;
            for (int i = 0; i < buffer.Length; i += 2)
            {
                if (i + 1 >= buffer.Length) break;
                short sample = (short)((buffer[i + 1] << 8) | buffer[i]);
                sum += Mathf.Abs(sample / 32768f); // Normalize to 0-1
            }

            float average = sum / (buffer.Length / 2);
            _currentInputLevel = Mathf.Max(_currentInputLevel, average);
        }

        public int GetActiveSpeakerCount()
        {
            return _voicePlayers.Count;
        }

        public float GetInputLevel()
        {
            return _currentInputLevel;
        }

        public float GetVoiceRange()
        {
            return MAX_VOICE_DISTANCE;
        }

        public override void _ExitTree()
        {
            StopVoiceCapture();
            _waveIn?.Dispose();

            foreach (var player in _voicePlayers.Values)
            {
                player.QueueFree();
            }
            _voicePlayers.Clear();
        }
    }
}
