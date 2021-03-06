﻿using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using UnityEngine.UI;
using UnityEngine.UI.Extensions;
using System;
using UnityEngine.SceneManagement;

public enum Direction
{
	Clockwise,
	CounterClockwise
}

public enum GameMode
{
	Move,
	Summon
}

public class GameController : MonoBehaviour 
{
	public float moveTime = 1;

	public int startingCharacterCount = 3;

	public List<Transform> characterMarkTransforms;

	public CanvasGroup rootCanvasGroup;

	public Transform characterHolder;
	public Transform circleCenter;

	public Image joystickIndicator;

	public List<GameMove> currentIncantation = new List<GameMove>();
	public UILineRenderer incantationLine;
	public UILineRenderer incantationPreview;

	public CharacterData mainCharacterData;

	public int goalCharacterNumber = 3;
	public Action<Action> goalCharactersChanged;
	public Action<int, int, Action> addedTurns;

	public int startRoundTurns;
	public int gainTurnsPerRound;
	public int maxTurns = 15;

	public Image splashMessage;
	public Sprite danceSplashMessage;
	public Sprite partyOverSplashMessage;

	private List<CharacterData> goalCharacters = new List<CharacterData>();
	public IEnumerable<CharacterData> currentGoalCharacters {
		get
		{
			return goalCharacters.AsEnumerable();
		}
	}

	private Character mainCharacter;
	private List<Character> characters = new List<Character>();
	private List<CharacterMark> characterMarks = new List<CharacterMark>();
	private Dictionary<CharacterMark, Character> characterMarkSlots = new Dictionary<CharacterMark, Character>();

	private Coroutine eventCoroutine = null;

	public GameMode gameMode
	{
		get
		{
			return performingIncantation == null ? GameMode.Move : GameMode.Summon;
		}
	}

	public class CharacterMark
	{
		public Transform transform;
	}

	public void Start()
	{
		foreach (var markTransform in characterMarkTransforms)
		{
			characterMarks.Add(new CharacterMark() { transform = markTransform });
		}
			
		mainCharacter = Character.Create(mainCharacterData);

		characters.Add(mainCharacter);

		mainCharacter.transform.SetParent(characterHolder);

		var characterMarksToAddTo = characterMarks.TakeRandom(characters.Count).GetEnumerator();
		foreach (var character in characters)
		{
			characterMarksToAddTo.MoveNext();
			characterMarkSlots.Add(characterMarksToAddTo.Current, character);
		}

		var added = characterMarkSlots.Keys.ToArray();
		foreach (var mark in characterMarks.Except(added))
		{
			characterMarkSlots.Add(mark, null);
		}

		roundTurns = startRoundTurns;

		RunEvent(StartGame());
	}

	private int highlightedIndex;
	public void Update()
	{
		var joyVector = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));

		var joyMag = joyVector.magnitude;

		var indicatorRectTransform = joystickIndicator.GetComponent<RectTransform>();

		var joyPos = joyVector * indicatorRectTransform.parent.GetComponent<RectTransform>().rect.width / 2 + (Vector2)indicatorRectTransform.position;
		var orderedList = (from mark in characterMarks select new { mark = mark, distance = ((Vector2)mark.transform.position - joyPos).sqrMagnitude }).ToList();
		orderedList.Sort((x, y) => (int)Mathf.Sign(x.distance - y.distance));

		var selectedMark = orderedList.First().mark;

		Vector2 finalPos;
		RectTransformUtility.ScreenPointToLocalPointInRectangle(
			indicatorRectTransform.parent.GetComponent<RectTransform>(), 
			RectTransformUtility.WorldToScreenPoint(null, selectedMark.transform.position),
			null,
			out finalPos);

		indicatorRectTransform.anchoredPosition = finalPos;

		if (joyMag < 0.8f)
		{
			highlightedIndex = -1;
			joystickIndicator.GetComponent<CanvasGroup>().alpha = Mathf.Min(joyMag, 0.8f);
		}
		else
		{
			highlightedIndex = characterMarks.IndexOf(selectedMark);
			joystickIndicator.GetComponent<CanvasGroup>().alpha = 1.0f;
		}

		foreach (var slot in characterMarkSlots)
		{
			if (slot.Value != null) 
				slot.Value.transform.position = slot.Key.transform.position;
		}

		if (eventCoroutine == null)
		{
			if (gameMode == GameMode.Move)
			{
				UpdateMoveState();
			}
			else
			{
				UpdateIncantState();
			}
		}
	}

	private IEnumerator SplashMessage(Sprite sprite, float delay = 0.5f)
	{
		splashMessage.sprite = sprite;
		splashMessage.SetNativeSize();
		splashMessage.color = Color.clear;

		var t = 0f;
		while (t < 1)
		{
			splashMessage.color = Color.Lerp(Color.clear, Color.white, t);
			splashMessage.transform.localScale = Vector3.Lerp(Vector3.one * 0.9f, Vector3.one, t);
			yield return new WaitForEndOfFrame();
			t += Time.deltaTime / 0.2f;
		}

		yield return new WaitForSeconds(delay);

		t = 0f;
		while (t < 1)
		{
			splashMessage.color = Color.Lerp(Color.white, Color.clear, t);
			splashMessage.transform.localScale = Vector3.Lerp(Vector3.one, Vector3.one * 0.9f, t);
			yield return new WaitForEndOfFrame();
			t += Time.deltaTime / 0.2f;
		}

		splashMessage.color = Color.clear;
	}

	private void UpdateMoveState()
	{
		HandleRound();

		MoveType type = MoveType.None;
		Direction direction = Direction.Clockwise;

		var startIndex = GetIndexOfCharacter(mainCharacter);
		var endIndex = highlightedIndex;

		var a = startIndex >= 0 && startIndex < characterMarks.Count ? characterMarks[startIndex] : null;
		var b = endIndex >= 0 && endIndex < characterMarks.Count ? characterMarks[endIndex] : null;

		if (a != null && b != null)
		{
			var difference = GetIndexDifference(startIndex, endIndex);
			var absDiff = Mathf.Abs(difference);
			Debug.Log(string.Format("Difference: {0}, {1} = {2}", startIndex, endIndex, difference));

			if (absDiff == 1)
			{
				type = MoveType.Shift;
				direction = difference > 0 ? Direction.Clockwise : Direction.CounterClockwise;

				if (a != null && b != null) SetIncantationPreview( t => MoveTypeMath.EvaluateCircle(a.transform.position, b.transform.position, circleCenter.position, t, false) );
			}
			else if (absDiff == characterMarks.Count / 2)
			{
				type = MoveType.Cross;

				if (a != null && b != null) SetIncantationPreview( t => MoveTypeMath.EvaluateLine(a.transform.position, b.transform.position, t) );
			}
			else if (absDiff > 1)
			{
				type = MoveType.DoSiDo;

				if (a != null && b != null) SetIncantationPreview( t => MoveTypeMath.EvaluateCircle(a.transform.position, b.transform.position, circleCenter.position, t, true) );
			}
			else
			{
				ClearIncantationPreview();
			}
		}
		else
		{
			ClearIncantationPreview();
		}

		if (Input.GetButton("Cross"))
		{
			switch(type)
			{
			case MoveType.Shift:
				RunEvent(DoRotate(direction));
				break;
			case MoveType.DoSiDo:
				RunEvent(DoSwap(a, b, true));
				break;
			case MoveType.Cross:
				RunEvent(DoSwap(a, b, false));
				break;
			}

			if (type != MoveType.None) 
				roundTurns--;
		}
		else if (Input.GetButton("Incant"))
		{
			RunEvent(DoIncant(endIndex));
		}
	}

	public void UpdateIncantState()
	{
		if (characterMarkSlots.All(x => x.Value != null))
		{
			RunEvent(GameOver());
		}

		if (Input.GetButton("Cross"))
		{
			var index = highlightedIndex;
			if (index != -1 && characterMarkSlots[characterMarks[index]] == null)
			{
				RunEvent(DoIncant(index));
			}
		}
	}

	private IEnumerator StartGame()
	{
		var t = 0f;
		while (t < 1)
		{
			rootCanvasGroup.alpha = t;
			yield return new WaitForEndOfFrame();
			t += Time.deltaTime;
		}

		yield return new WaitForSeconds(0.2f);

		SoundController.main.StartMusic();

		SoundController.main.PlaySound("laugh");

		yield return SplashMessage(danceSplashMessage);

		yield return StartCoroutine(AddNewGoalCharacter());
	}

	private int roundTurns = 0;
	public int roundTurnsRemaining
	{
		get
		{
			return roundTurns;
		}
	}

	private void HandleRound()
	{
		var matchCharacters = goalCharacters.ToList();
		foreach (var character in characters)
		{
			matchCharacters.Remove(character.characterData);
		}
		var matchedAllCharacters = matchCharacters.Count == 0;
		if (matchedAllCharacters)
		{
			RunEvent(RoundSuccess());
			return;
		}

		if (roundTurns <= 0)
		{
			RunEvent(GameOver());
			return;
		}
	}

	private IEnumerator GameOver()
	{

		SoundController.main.StopMusic();

		SoundController.main.PlaySound("evil_summon");
		yield return StartCoroutine(SplashMessage(partyOverSplashMessage, 4f));

		var t = 0f;
		while (t < 1)
		{
			rootCanvasGroup.alpha = 1.0f - t;
			yield return new WaitForEndOfFrame();
			t += Time.deltaTime;
		}

		SceneManager.LoadScene("Menu");
	}

	private IEnumerator RoundSuccess()
	{
		var before = roundTurns;
		roundTurns += gainTurnsPerRound;
		roundTurns = Mathf.Min(roundTurns, maxTurns);
		var remainingFeedback = 0;
		if (addedTurns != null)
		{
			remainingFeedback = addedTurns.GetInvocationList().Length;

			addedTurns(before, roundTurns, () => remainingFeedback--);
		}
		while (remainingFeedback > 0)
			yield return new WaitForEndOfFrame();

		yield return StartCoroutine(AddNewGoalCharacter());
	}

	private IEnumerator AddNewGoalCharacter()
	{
		goalCharacters.Add(CharacterData.allCharacterData.Except(Enumerable.Repeat(mainCharacter.characterData, 1)).PickRandom(1).First());
		if (goalCharacters.Count > goalCharacterNumber)
		{
			goalCharacters.RemoveAt(0);
		}

		var remainingFeedback = 0;
		if (goalCharactersChanged != null)
		{
			remainingFeedback = goalCharactersChanged.GetInvocationList().Length;

			goalCharactersChanged(() => remainingFeedback--);
		}
		while (remainingFeedback > 0)
			yield return new WaitForEndOfFrame();
	}

	private void OnCharacterMoved(Character character, MoveType moveType, CharacterMark fromMark, CharacterMark toMark)
	{
		if (character == mainCharacter)
		{
			currentIncantation.Add(new GameMove() { type = moveType, fromMark = fromMark, toMark = toMark, circle = this }); 
		}
	}

	private void ClearIncantationPreview()
	{
		incantationPreview.Clear();
	}

	private void SetIncantationPreview(Func<float, Vector2> line)
	{
		incantationPreview.SetPoints((from i in Enumerable.Range(0, 100) select TransformIncantationPoint(line(i / 100.0f))).ToArray()); 
	}

	private void AddIncantationPoint(Vector3 point)
	{
		incantationLine.AddPoint(TransformIncantationPoint(point));
	}

	private Vector2 TransformIncantationPoint(Vector3 point)
	{
		Vector2 transformed;
		RectTransformUtility.ScreenPointToLocalPointInRectangle(incantationLine.rectTransform, (RectTransformUtility.WorldToScreenPoint(null, point)), null, out transformed);
		var rect = incantationLine.rectTransform.rect;
		var offset = (transformed - rect.min);
		var scaled = new Vector2(offset.x / rect.width, offset.y / rect.height);
		return scaled;
	}

	public int GetIndexDifference(int startIndex, int endIndex)
	{
		var didSwap = false;
		if (startIndex > endIndex) {
			var swap = startIndex;
			startIndex = endIndex;
			endIndex = swap;
			didSwap = true;
		}

		var optionA = endIndex - startIndex;
		var optionB = startIndex - (endIndex - characterMarks.Count);
		if (optionA < optionB)
		{
			return didSwap ? -optionA : optionA;
		}
		else
		{
			return didSwap ? optionB : -optionB;
		}
	}

	public int GetIndexInDirection(Vector2 vec)
	{
		var joyDirection = Vector2Extensions.FullAngle(Vector2.right, vec);
		var degreesBetweenMarks = 360.0f / characterMarks.Count;
		var index = joyDirection / degreesBetweenMarks;
		var portion = index - (int)index;
		return Mathf.RoundToInt(index) % characterMarks.Count;
	}

	public int GetIndexOfCharacter(Character character)
	{
		foreach (var pair in characterMarkSlots)
		{
			if (pair.Value == character)
				return characterMarks.IndexOf(pair.Key);
		}
		return -1;
	}

	public Character GetCharacterAtIndex(int markIndex)
	{
		while (markIndex < 0) markIndex += characterMarks.Count;
		markIndex %= characterMarks.Count;

		return characterMarkSlots[characterMarks[markIndex]];
	}

	public void RunEvent(IEnumerator move)
	{
		StartCoroutine(RunThenClear(move));
	}

	public IEnumerator RunThenClear(IEnumerator coroutine)
	{
		eventCoroutine = StartCoroutine(coroutine);
		yield return eventCoroutine;
		eventCoroutine = null;
	}

	private IncantationData performingIncantation;
	public void RefreshIncantation()
	{
		performingIncantation = IncantationData.GetIncantationData(this.currentIncantation);
	}

	private class Shift
	{
		public Shift(MoveType moveType, Character character, CharacterMark fromMark, CharacterMark toMark, Transform circleCenter )
		{
			this.moveType = moveType;
			this.character = character;
			this.fromMark = fromMark;
			this.toMark = toMark;
			this.circleCenter = circleCenter;
		}

		public MoveType moveType;
		public Character character;
		public CharacterMark fromMark;
		public CharacterMark toMark;

		private Transform circleCenter;

		public Vector2 Interpolate(float t)
		{
			if (character == null) return Vector2.zero;

			switch (moveType)
			{
			case MoveType.Cross:
				character.transform.position = MoveTypeMath.EvaluateLine(fromMark.transform.position, toMark.transform.position, t);
				break;
			case MoveType.DoSiDo:
				character.transform.position = MoveTypeMath.EvaluateCircle(fromMark.transform.position, toMark.transform.position, circleCenter.position, t, true);
				break;
			case MoveType.Shift:
				character.transform.position = MoveTypeMath.EvaluateCircle(fromMark.transform.position, toMark.transform.position, circleCenter.position, t, false);
				break;
			}

			return character.transform.position;
		}
	}

	public class GameMove
	{
		public MoveType type;
		public GameController.CharacterMark fromMark;
		public GameController.CharacterMark toMark;
		public GameController circle;

		public bool MatchDataMove(IncantationData.Move move)
		{
			var fromMarkIndex = circle.characterMarks.IndexOf(fromMark);
			var toMarkIndex = circle.characterMarks.IndexOf(toMark);

			var directionIsClockwise = Vector2Extensions.SignedAngle((fromMark.transform.position - circle.circleCenter.position), (toMark.transform.position - circle.circleCenter.position)) > 0;
			var direction = directionIsClockwise ? Direction.Clockwise : Direction.CounterClockwise;

			Debug.Log("Move Compare: " + type + " vs " + move.type + " dir: " + direction + " vs " + move.direction);

			return type == move.type && (type == MoveType.Cross || direction == move.direction);
		}
	}

	public IEnumerator DoSwap(CharacterMark a, CharacterMark b, bool doSiDo)
	{
		var characterDisplayA = characterMarkSlots[a];
		var characterDisplayB = characterMarkSlots[b];

		characterMarkSlots[a] = null;
		characterMarkSlots[b] = null;

		var shiftA = new Shift(doSiDo ? MoveType.DoSiDo : MoveType.Cross, characterDisplayA, a, b, circleCenter);
		var shiftB = new Shift(doSiDo ? MoveType.DoSiDo : MoveType.Cross, characterDisplayB, b, a, circleCenter);

		SoundController.main.PlaySound("swap");

		var t = 0f;
		while (t < 1)
		{
			var ptA = shiftA.Interpolate(t);
			var ptB = shiftB.Interpolate(t);

			if (characterDisplayA == mainCharacter)
			{
				AddIncantationPoint(ptA);
			}

			if (characterDisplayB == mainCharacter)
			{
				AddIncantationPoint(ptB);
			}

			yield return new WaitForEndOfFrame();
			t += Time.deltaTime / moveTime;
		}

		characterMarkSlots[a] = characterDisplayB;
		characterMarkSlots[b] = characterDisplayA;

		OnCharacterMoved(characterDisplayA, doSiDo ? MoveType.DoSiDo : MoveType.Cross, a, b);
		OnCharacterMoved(characterDisplayB, doSiDo ? MoveType.DoSiDo : MoveType.Cross, b, a);

		yield return StartCoroutine(CheckBeef(characterDisplayA));
		yield return StartCoroutine(CheckBeef(characterDisplayB));

		RefreshIncantation();
	}

	public IEnumerator DoRotate(Direction direction, CharacterMark skip = null)
	{
		var characterMarks = skip == null ? this.characterMarks : this.characterMarks.Except(Enumerable.Repeat(skip, 1)).ToList();
		var shifts = new List<Shift>();

		for (var i = 0; i < characterMarks.Count; i++)
		{
			var current = characterMarks[i];

			var nextIndex = direction == Direction.Clockwise ? i + 1 : i - 1;
			nextIndex += characterMarks.Count;
			nextIndex %= characterMarks.Count;

			var next = characterMarks[nextIndex];

			var currentRealIndex = this.characterMarks.IndexOf(current);
			var currentNextIndex = this.characterMarks.IndexOf(next);
			var isDoSiDo = !((currentRealIndex - 1 + this.characterMarks.Count) % this.characterMarks.Count == currentNextIndex ||
				(currentRealIndex + 1) % this.characterMarks.Count == currentNextIndex);

			shifts.Add(new Shift(isDoSiDo ? MoveType.DoSiDo : MoveType.Shift, characterMarkSlots[current], current, next, circleCenter));
		}

		foreach (var mark in characterMarks)
		{
			characterMarkSlots[mark] = null;
		}

		SoundController.main.PlaySound("swap");
		var t = 0f;
		while (t < 1)
		{
			foreach (var shift in shifts)
			{
				var pt = shift.Interpolate(t);
				if (shift.character == mainCharacter)
				{
					AddIncantationPoint(pt);
				}
			}
			yield return new WaitForEndOfFrame();
			t += Time.deltaTime / moveTime;
		}

		foreach (var shift in shifts)
		{
			characterMarkSlots[shift.toMark] = shift.character;

			OnCharacterMoved(shift.character, shift.moveType, shift.fromMark, shift.toMark);

			yield return StartCoroutine(CheckBeef(shift.character));
		}

		RefreshIncantation();
	}

	[SerializeField]
	private float glowTime = 1.0f;
	private IEnumerator DoIncant(int targetIndex)
	{
		var originalIncantationLineColor = incantationLine.color;

		this.currentIncantation = new List<GameMove>();

		var t = 0f;
		while (t < 1)
		{
			this.incantationLine.color = Color.Lerp(originalIncantationLineColor, performingIncantation != null ? Color.white : Color.red, t);
			yield return new WaitForEndOfFrame();
			t += Time.deltaTime / glowTime;
		}

		this.incantationLine.color = originalIncantationLineColor;

		incantationLine.Clear();

		if (performingIncantation != null)
		{
			var targetMark = characterMarks[targetIndex];

			var character = Character.Create(performingIncantation.summon);
			character.transform.SetParent(characterHolder);
			characters.Add(character);

			SoundController.main.PlaySound("succeed");

			characterMarkSlots[targetMark] = character;

			t = 0f;
			while (t < 1)
			{
				character.displayImage.color = Color.Lerp(Color.clear, Color.white, t);
				yield return new WaitForEndOfFrame();
				t += Time.deltaTime / 0.4f;
			}

			yield return StartCoroutine(CheckBeef(character));

			performingIncantation = null;
		}
	}

	[SerializeField]
	private float removalFadeTime = 0.2f;
	private IEnumerator CheckBeef(Character aggressor)
	{
		if (aggressor == null)
		{
			yield break;
		}

		var agressorIndex = GetIndexOfCharacter(aggressor);
		var left = (agressorIndex - 1 + characterMarks.Count) % characterMarks.Count;
		var right = agressorIndex + 1;
		var leftCharacter = GetCharacterAtIndex(left);
		var rightCharacter = GetCharacterAtIndex(right);

		var removingCharacters = new List<Character>();

		if (leftCharacter != null && aggressor.characterData.beefCharacter == leftCharacter.characterData)
		{
			removingCharacters.Add(leftCharacter);
		}

		if (rightCharacter != null && aggressor.characterData.beefCharacter == rightCharacter.characterData)
		{
			removingCharacters.Add(rightCharacter);
		}

		if (removingCharacters.Count == 0)
			yield break;

		aggressor.ShowBeef();

		yield return new WaitForSeconds(0.5f);

		SoundController.main.PlaySound("disappear");

		var t = 0f;
		while (t < 1.0f)
		{
			foreach (var character in removingCharacters)
			{
				character.displayImage.color = Color.Lerp(Color.white, Color.red, t);
			}
			yield return new WaitForEndOfFrame();
			t += Time.deltaTime;
		}

		t = 0f;
		while (t < 1.0f)
		{
			foreach (var character in removingCharacters)
			{
				character.displayImage.color = Color.Lerp(Color.red, new Color(1f, 0f, 0f, 0f), t);
			}
			yield return new WaitForEndOfFrame();
			t += Time.deltaTime;
		}

		foreach (var character in removingCharacters)
		{
			characters.Remove(character);
			foreach (var key in characterMarkSlots.Keys.ToArray())
			{
				if (characterMarkSlots[key] == character)
					characterMarkSlots[key] = null;
			}
		}
	}

	private Vector2[] FindCircleCenter(Vector2 a, Vector2 b, float r)
	{
		var diff = (b - a);
		var mag = diff.magnitude;

		var midpoint = a + diff.normalized * mag / 2;

		var q = Mathf.Sqrt(Mathf.Pow(r, 2) - Mathf.Pow((mag / 2), 2));

		return new Vector2[] {
			new Vector2(
				midpoint.x + q * (a.y - b.y) / mag,
				midpoint.y + q * (b.x - a.x) / mag
			),
			new Vector2(
				midpoint.x - q * (a.y - b.y) / mag,
				midpoint.y - q * (b.x - a.x) / mag
			)
		};
	}

}
