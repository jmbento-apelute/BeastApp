internal sealed class ApplicationState
{
    private readonly object syncRoot = new();
    private ILiveFrameSource activeSource;
    private SceneContext? sceneContext;
    private DateTimeOffset voiceInputSuppressedUntil = DateTimeOffset.MinValue;
    private LiveVideoEffect liveEffect = LiveVideoEffect.Normal;

    public ApplicationState(ILiveFrameSource initialSource)
    {
        activeSource = initialSource;
    }

    public ILiveFrameSource ActiveSource
    {
        get
        {
            lock (syncRoot)
            {
                return activeSource;
            }
        }
    }

    public SceneContext? SceneContext
    {
        get
        {
            lock (syncRoot)
            {
                return sceneContext;
            }
        }
    }

    public DateTimeOffset VoiceInputSuppressedUntil
    {
        get
        {
            lock (syncRoot)
            {
                return voiceInputSuppressedUntil;
            }
        }
    }

    public void SelectSource(ILiveFrameSource source)
    {
        lock (syncRoot)
        {
            activeSource = source;
        }
    }

    public ILiveFrameSource ToggleSource(ILiveFrameSource first, ILiveFrameSource second)
    {
        lock (syncRoot)
        {
            activeSource = ReferenceEquals(activeSource, first) ? second : first;
            return activeSource;
        }
    }

    public void UpdateScene(SceneContext context)
    {
        lock (syncRoot)
        {
            sceneContext = context;
        }
    }

    public void SuppressVoiceInput(TimeSpan duration)
    {
        lock (syncRoot)
        {
            voiceInputSuppressedUntil = DateTimeOffset.UtcNow + duration;
        }
    }

    public LiveVideoEffect GetLiveEffect()
    {
        lock (syncRoot)
        {
            return liveEffect;
        }
    }

    public LiveVideoEffect RotateLiveEffect()
    {
        lock (syncRoot)
        {
            liveEffect = LiveVideoEffects.Next(liveEffect);
            LiveVideoEffects.ResetTemporalState();
            return liveEffect;
        }
    }
}
