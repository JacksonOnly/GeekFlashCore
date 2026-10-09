using GeekFlashCore.Protocol.Mtk.Da;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol : IMtkDaReconnect
{
    public MtkEntrySignal InspectEntrySignal(CancellationToken cancellationToken = default) => Execute(() =>
    {
        if (_state != MtkSessionState.Disconnected) throw new InvalidOperationException(Strings.SessionUnavailable);
        if (!_transport.IsOpen) { _transport.Open(); _openedHere = true; }
        return _wire.InspectEntrySignal();
    }, cancellationToken);

    public void ResumeDownloadAgent(MtkBootStage stage, MtkTargetInfo target, MtkConnectionResources resources,
        MtkStorageInfo? legacyStorage = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resources);
        Execute(() =>
        {
            if (_state != MtkSessionState.Disconnected || stage is not (MtkBootStage.Da1 or MtkBootStage.Da2))
                throw new InvalidOperationException(Strings.SessionUnavailable);
            ValidateResources(resources, target, requireBootAuthentication: false);
            if (stage == MtkBootStage.Da2 && resources.DownloadAgent.Entry.Kind == MtkDaKind.Legacy)
                LegacySession.ValidateResumeStorage(legacyStorage);
            _wire.Begin(cancellationToken, _options.ConnectTimeoutMilliseconds);
            State(MtkSessionState.Opening);
            try { if (!_transport.IsOpen) { _transport.Open(); _openedHere = true; } }
            catch { _state = MtkSessionState.Disconnected; throw; }
            _target = target with { Stage = stage };
            _initialTarget = _target;
            _image = resources.DownloadAgent;
            _da1Authentication = _da2Authentication = MtkDaAuthenticationState.NotQueried;
            _wire.Stage = stage;
            if (stage == MtkBootStage.Da1)
            {
                LoadDa(resources, _target, upload: false);
                if (_da is XmlSession) AuthenticateDaSynchronously(MtkAuthenticationKind.Da1Sla, resources);
                ContinueDa(resources, _target);
            }
            else
            {
                _da = _image.Entry.Kind switch
                {
                    MtkDaKind.XFlash => new XFlashSession(_wire, _options),
                    MtkDaKind.Xml => new XmlSession(_wire, _options),
                    _ => new LegacySession(_wire, _options)
                };
                State(MtkSessionState.Da2Ready);
                if (_da is LegacySession legacy) legacy.Resume(legacyStorage!);
            }
            AuthenticateDaSynchronously(MtkAuthenticationKind.DaSla, resources);
            CompleteDaAuthentication();
            CompleteConnection();
            return 0;
        }, cancellationToken);
    }
}
