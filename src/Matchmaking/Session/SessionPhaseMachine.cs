using System.Collections.Generic;

namespace PeakCSR
{
    // 阶段状态机: 按会话状态切换 IMatchPhase, 阶段切换时回调 Enter/Exit
    public sealed class SessionPhaseMachine
    {
        private readonly Dictionary<CsMatchState, IMatchPhase> _phases = new Dictionary<CsMatchState, IMatchPhase>();
        private readonly IMatchPhase _fallback;
        private MatchSession _currentSession;
        private IMatchPhase _currentPhase;

        public SessionPhaseMachine(IMatchPhase fallback)
        {
            _fallback = fallback;
        }

        public void Register(CsMatchState state, IMatchPhase phase)
        {
            _phases[state] = phase;
        }

        public void Tick(MatchSession session)
        {
            IMatchPhase target;
            if (!_phases.TryGetValue(session.State, out target)) target = _fallback;

            if (!ReferenceEquals(session, _currentSession) || !ReferenceEquals(target, _currentPhase))
            {
                if (_currentPhase != null) _currentPhase.Exit(_currentSession);
                _currentSession = session;
                _currentPhase = target;
                target.Enter(session);
            }

            target.Tick(session);
        }
    }
}
