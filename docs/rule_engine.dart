part of live_scoring_core.entities.outdoor_cricket;

class RuleEngine {
  static const int HomeRunScore = 12;
  static const int SubsequentBowlerExtrasScore = 3;

  final dtos.OutdoorCricketFixtureState _fixtureState;

  RuleEngine(this._fixtureState);

  /*LMS Specific rules*/
  int _getExtrasScore(dtos.Over over, dtos.Ball ball) {
    if(_fixtureState.innings.length == 2) {
      if (over.overNumber < _fixtureState.secondInningsOvers &&
          _getOtherExtrasThisOver(over, ball).length > 0)
        return SubsequentBowlerExtrasScore;
    } else {
      if (over.overNumber < _fixtureState.firstInningsOvers &&
          _getOtherExtrasThisOver(over, ball).length > 0)
        return SubsequentBowlerExtrasScore;
    }

    return 1;
  }

  bool _ballShouldIncreaseOverCount(dtos.Over over, dtos.Ball ball) {
    var otherExtras = _getOtherExtrasThisOver(over, ball);
    if(_fixtureState.innings.length == 2) {
      if (over.overNumber < _fixtureState.secondInningsOvers) {
        return otherExtras.length > 0 ||
            !BallExtensionMethods.containsBowlerExtras(ball);
      } else {
        return !BallExtensionMethods.containsBowlerExtras(ball);
      }
    } else {
      if (over.overNumber < _fixtureState.firstInningsOvers) {
        return otherExtras.length > 0 ||
            !BallExtensionMethods.containsBowlerExtras(ball);
      } else {
        return !BallExtensionMethods.containsBowlerExtras(ball);
      }
    }
  }

  Iterable<dtos.OutdoorCricketEvent> _getOtherExtrasThisOver(dtos.Over over, dtos.Ball ball) {
    return over.events.where((x) => x != ball).where((x) => (x is dtos.Ball && BallExtensionMethods.containsBowlerExtras(x)));
  }

  bool get _extrasCountToBatsmanScore => true;

  bool _isHomeRun(dtos.OutdoorCricketEvent overEvent, int runs) {
    var over = _fixtureState.getCurrentOver();
    if(_fixtureState.innings.length == 2) {
      return over.overNumber == _fixtureState.secondInningsOvers &&
          over.events.last == overEvent &&
          _numberOfBallsThatCount(
              over, over.events.where((x) => x != overEvent)) ==
              _fixtureState.ballsPerOver - 1 &&
          runs == 6;
    } else {
      return over.overNumber == _fixtureState.firstInningsOvers &&
          over.events.last == overEvent &&
          _numberOfBallsThatCount(
              over, over.events.where((x) => x != overEvent)) ==
              _fixtureState.ballsPerOver - 1 &&
          runs == 6;
    }
  }

  int _getRunsToAdd(dtos.OutdoorCricketEvent overEvent, dtos.Runs runs) {
    var runsToAdd = runs.runs;

    if (_isHomeRun(overEvent, runs.runs)) runsToAdd = HomeRunScore;

    return runsToAdd;
  }

  void _addHomeRuns(dtos.Ball ball, dtos.Runs runs) {
    if (_isHomeRun(ball, runs.runs)) ball.striker.homeRuns++;
  }

  /*End LMS specific rules*/

  //Can be removed if we keep a count of the number of balls that count in the overs property of things?
  int _numberOfBallsThatCount(dtos.Over over, Iterable<dtos.OutdoorCricketEvent> overEvents) {
    return overEvents.where((x) => x is dtos.Ball).where((x) => _ballShouldIncreaseOverCount(over, x as dtos.Ball)).length;
  }

  void applyEvent(dtos.Ball ball) {
    //REFACTOR: Consider splitting rule engine into parts
    _processOverCounts(ball);
    _processRuns(ball);
    _processExtras(ball);
    _processWickets(ball);
  }

  void _processOverCounts(dtos.Ball ball) {
    var over = _fixtureState.getCurrentOver();
    if (_ballShouldIncreaseOverCount(over, ball)) {
      OutdoorCricketFixtureStateExtensionMethods.incrementBallCount(_fixtureState);
      if (over.completed) {
        if (OverExtensionMethods.isMaiden(over)) {
          _fixtureState.currentBowler.maidens++;
        }
      }
    }
    ball.striker.ballsFaced++;
  }

  void _processRuns(dtos.Ball ball) {
    for (dtos.DotBall dots in ball.ballResults.where((x) => x is dtos.DotBall)) {
      ball.striker.battingDotBalls += 1;
      ball.bowler.bowlingDotBalls += 1;
    }
    for (dtos.Runs runs in ball.ballResults.where((x) => x is dtos.Runs)) {
      var runsToAdd = _getRunsToAdd(ball, runs);

      _fixtureState.getCurrentInnings().score.runs += runsToAdd;
      ball.striker.runsScored += runsToAdd;
      ball.bowler.runsConceded += runsToAdd;

      //LMS Specific: Should be moved somewhere...
      _addHomeRuns(ball, runs);

      if (runs.runs == 4)
        ball.striker.fours++;
      else if (runs.runs == 6) ball.striker.sixes++;

    }
  }

  void _processWickets(dtos.Ball ball) {
    for (dtos.Wicket wicket in ball.ballResults.where((x) => x is dtos.Wicket)) {
      _fixtureState.getCurrentInnings().score.wickets++;
      if (wicket is dtos.BowlerCreditedWithWicket) ball.bowler.wickets++;
      if (wicket is dtos.Stumped) ball.keeper.stumpings++;
      if (wicket is dtos.Caught && wicket.catcher != null && wicket.catcher.id == ball.keeper.id) ball.keeper.catches++;
      if (wicket is dtos.Caught && wicket.catcher != null && wicket.catcher.id == ball.fielder.id) ball.fielder.catches++;
      if (wicket is dtos.RunOut && wicket.thrower != null && wicket.thrower.id == ball.fielder.id) ball.fielder.runOuts++;
      if (wicket is dtos.DoublePlay && wicket.fielder != null && wicket.fielder.id == ball.fielder.id) ball.fielder.doublePlay++;
    }
  }

  void _processExtras(dtos.Ball ball) {
    for (dtos.Extras extras in ball.ballResults.where((x) => x is dtos.Extras)) {
      if (extras is dtos.NoBall || extras is dtos.Wide) {
        _setExtrasFromNoBallAndWideBall(ball, extras);
      }
      else {
        _setExtraAdditionalRunsNotFromBat(
            _fixtureState.getCurrentInnings(), _fixtureState.currentBowler,
            extras, ball);
      }
    }
  }

  void _setExtrasFromNoBallAndWideBall(dtos.Ball overEvent, dtos.Extras extras) {
    var totalExtrasScore = _getExtrasScore(_fixtureState.getCurrentOver(), overEvent) + extras.additionalRunsNotFromBat;
    _fixtureState.getCurrentInnings().score.runs += totalExtrasScore;
    _fixtureState.currentBowler.runsConceded += totalExtrasScore;
    if (_extrasCountToBatsmanScore) overEvent.striker.runsScored += totalExtrasScore;
    if (extras is dtos.NoBall) overEvent.bowler.noBall += 1;
    if (extras is dtos.Wide) overEvent.bowler.wide += 1;
  }

  void _setExtraAdditionalRunsNotFromBat(dtos.Innings innings, dtos.Bowler bowler, dtos.Extras extras, dtos.Ball ball) {
    innings.score.runs += extras.additionalRunsNotFromBat;
    innings.totalExtras += extras.additionalRunsNotFromBat;
    if (!(extras is dtos.Bye || extras is dtos.LegBye)) bowler.runsConceded += extras.additionalRunsNotFromBat;
    if(extras is dtos.Bye) ball.keeper.byes += extras.additionalRunsNotFromBat;
    print("_setExtraAdditionalRunsNotFromBat Non Striker " + ball.nonStriker.id.toString());
    if (extras is dtos.Steal) ball.nonStriker.steal += 1;
  }
}
