﻿using System.Net;
using PacketDotNet;
using VpnHood.Common.Collections;
using System.Threading.Tasks;
using System.Collections.Generic;
using VpnHood.Tunneling.Factory;
using System.Linq;
using System;
using VpnHood.Tunneling.Exceptions;
using VpnHood.Common.Timing;

namespace VpnHood.Tunneling;


public abstract class UdpProxyPool : IDisposable, IWatchDog
{
    private readonly ISocketFactory _socketFactory;
    private readonly TimeoutDictionary<string, UdpProxy> _connectionMap;
    private readonly List<UdpProxy> _udpProxies = new();
    private TimeSpan _udpTimeout = TimeSpan.FromSeconds(120);
    private bool _disposed;

    public abstract Task OnPacketReceived(IPPacket packet);
    public TimeoutDictionary<IPEndPoint, TimeoutItem<bool>> RemoteEndPoints { get; } = new(TimeSpan.FromSeconds(60));
    public int WorkerMaxCount { get; set; } = int.MaxValue;
    public int WorkerCount { get { DoWatch(); lock (_udpProxies) return _udpProxies.Count; } }
    public WatchDogChecker WatchDogChecker { get; } = new();
    public event EventHandler<EndPointEventArgs>? OnNewEndPoint;

    public TimeSpan UdpTimeout
    {
        get => _udpTimeout;
        set
        {
            _udpTimeout = value;
            _connectionMap.Timeout = value;
            RemoteEndPoints.Timeout = value;
            WatchDogChecker.Interval = value;
        }
    }

    protected UdpProxyPool(ISocketFactory socketFactory)
    {
        _socketFactory = socketFactory;
        _connectionMap = new TimeoutDictionary<string, UdpProxy>(UdpTimeout);
    }

    public ValueTask SendPacket(IPAddress sourceAddress, IPAddress destinationAddress, UdpPacket udpPacket, bool? noFragment)
    {
        var sourceEndPoint = new IPEndPoint(sourceAddress, udpPacket.SourcePort);
        var destinationEndPoint = new IPEndPoint(destinationAddress, udpPacket.DestinationPort);
        var addressFamily = destinationAddress.AddressFamily;
        DoWatch();

        // find the proxy for the connection (source-destination)
        var connectionKey = $"{sourceEndPoint}:{destinationEndPoint}";
        var udpWorker = _connectionMap.GetOrAdd(connectionKey, _ =>
        {
            // Find or create a worker that does not use the RemoteEndPoint
            lock (_udpProxies)
            {
                var newUdpWorker = _udpProxies.FirstOrDefault(x =>
                    x.AddressFamily == addressFamily &&
                    !x.DestinationEndPointMap.TryGetValue(destinationEndPoint, out var _));

                var isNewLocalEndPoint = false;
                if (newUdpWorker == null)
                {
                    // check WorkerMaxCount
                    if (_udpProxies.Count >= WorkerMaxCount)
                        throw new UdpClientQuotaException(_udpProxies.Count);

                    newUdpWorker = new UdpProxy(this, _socketFactory.CreateUdpClient(addressFamily), addressFamily);
                    _udpProxies.Add(newUdpWorker);
                    isNewLocalEndPoint = true;
                }

                // Add to RemoteEndPoints; DestinationEndPointMap may have duplicate RemoteEndPoints in different workers
                var isNewRemoteEndPoint = false;
                RemoteEndPoints.GetOrAdd(destinationEndPoint, _ =>
                {
                    isNewRemoteEndPoint = true;
                    return new TimeoutItem<bool>(true);
                });

                // Raise new endpoints
                OnNewEndPoint?.Invoke(this, new EndPointEventArgs(ProtocolType.Udp,
                    newUdpWorker.LocalEndPoint, destinationEndPoint, isNewLocalEndPoint, isNewRemoteEndPoint));

                // Add destinationEndPoint; each newUdpWorker can only have one destinationEndPoint
                newUdpWorker.DestinationEndPointMap.TryAdd(destinationEndPoint, new TimeoutItem<IPEndPoint>(sourceEndPoint));
                return newUdpWorker;
            }
        });

        var dgram = udpPacket.PayloadData ?? Array.Empty<byte>();
        return udpWorker.SendPacket(destinationEndPoint, dgram, noFragment);
    }

    public void DoWatch()
    {
        // remove useless workers
        lock (_udpProxies)
            TimeoutItemUtil.CleanupTimeoutList(_udpProxies, UdpTimeout);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock(_udpProxies)
            _udpProxies.ForEach(udpWorker => udpWorker.Dispose());

        _connectionMap.Dispose();
        RemoteEndPoints.Dispose();
    }
}