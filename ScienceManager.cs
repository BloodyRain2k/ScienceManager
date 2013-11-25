/*
 * Created by SharpDevelop.
 * User: Bernhard
 * Date: 20.10.2013
 * Time: 11:55
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using KSP.IO;
using UnityEngine;

public class ExperimentAssistant {
	public readonly ModuleScienceExperiment experiment;
	private bool _deploying;
	private bool _resetting;
	private bool _transmitting;
	private ModuleDataTransmitter transmitter;
	public float delay = 0;
	
	public ExperimentAssistant(ModuleScienceExperiment experiment) {
		this.experiment = experiment;
	}
	
	public bool deployed {
		get {
			return experiment.Deployed;
		}
	}
	
	public bool deploying {
		get {
			if (deployed) {
				_deploying = false;
			}
			return _deploying;
		}
	}
	
	public void Deploy() {
		if (!ready || deploying) {
			return;
		}
		_deploying = true;
		_resetting = false;
		experiment.DeployExperiment();
	}
	
	public bool ready {
		get {
			return !deployed && delay <= 0 && experiment.GetData()[0] == null;
		}
	}
	
	public bool resetting {
		get {
			if (ready) {
				_resetting = false;
			}
			return _resetting;
		}
	}
	
	public void Reset(float delay = 0) {
		if (resetting) {
			return;
		}
		if (delay > 0 && this.delay <= 0) {
			this.delay = delay;
		}
		_resetting = true;
		_deploying = false;
		experiment.ResetExperiment();
	}
	
	public bool transmitting {
		get {
			if (transmitter == null || !transmitter.IsBusy()) {
				_transmitting = false;
			}
			return _transmitting;
		}
	}
	
	public void Transmit() {
		if (transmitting || !deployed) {
			return;
		}
		var comm = (from c in experiment.vessel.FindPartModulesImplementing<ModuleDataTransmitter>() where !c.IsBusy() orderby c.DataResourceCost select c).FirstOrDefault();
		transmitter = comm;
		transmitter.TransmitData(new List<ScienceData>(experiment.GetData()));
		_transmitting = true;
		Reset();
	}
	
	public float transmitValue
	{
		get {
			var data = experiment.GetData()[0];
			if (data == null) {
				return 0f;
			}
			var subject = ResearchAndDevelopment.GetSubjectByID(data.subjectID);
			if (subject.science >= subject.scienceCap - 0.01f) {
				return 0f;
			}
			return Mathf.Min((data.dataAmount * data.transmitValue) / subject.dataScale * subject.subjectValue * subject.scientificValue, subject.scienceCap);
		}
	}
	
	public override string ToString() {
		return string.Format("[ExperimentAssistant: Deploying={0}, Resetting={1}, Transmitting={2}, Deployed={3}, Ready={4}]", _deploying, _resetting, _transmitting, deployed, ready);
	}
}

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class ScienceManager : MonoBehaviour
{
	string configFile = IOUtils.GetFilePathFor(typeof(ScienceManager), "ScienceManager.cfg");

	bool auto = false;
	bool windowShown = false;
	Rect windowPosSize = new Rect();
	string windowTitle = "Science Manager";
	Vector2 scrollPos = new Vector2();
	Vessel lastVessel;
	
	float returnScience = 0;
	float transmitScience = 0;
	float dataAmount = 0;
	
	Dictionary<string, float> subjectScienceValue = new Dictionary<string, float>();
	Dictionary<string, float> subjectScience = new Dictionary<string, float>();
	List<ExperimentAssistant> assistants = new List<ExperimentAssistant>();
	
	GUIStyle styleTitle = new GUIStyle(HighLogic.Skin.label);
	
	static MethodInfo miGatherData = typeof(ModuleScienceExperiment).GetMethod("gatherData", BindingFlags.NonPublic | BindingFlags.Instance);
	static MethodInfo miDumpData = typeof(ModuleScienceExperiment).GetMethod("dumpData", BindingFlags.NonPublic | BindingFlags.Instance);
	FieldInfo fiERDP = new List<FieldInfo>(typeof(ExperimentsResultDialog).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)).Find(f => f.FieldType == typeof(List<ExperimentResultDialogPage>));
	List<ExperimentResultDialogPage> pages;
	
	public void Start() {
		styleTitle.fontSize = 12;
		RenderingManager.AddToPostDrawQueue(0, new Callback (drawGUI));
		GameEvents.onGameSceneLoadRequested.Add(delegate (GameScenes scene) { save(); });
		load();
//		FlightGlobals.Bodies.ForEach(b => Debug.Log(b.bodyName + " - " + b.scienceValues.ToString()));
	}

	void save() {
		ConfigNode cn = new ConfigNode();
		var pos = new Vector2(windowPosSize.x, windowPosSize.y);
		cn.AddValue("windowPos", pos);
		if (!File.Exists<ScienceManager>(configFile)) {
			File.Create<ScienceManager>(configFile).Dispose();
			// how the hell do you create a folder so ConfigNode.Save doesn't error if it's folder's missing?
		}
		if (pos != Vector2.zero) { cn.Save(configFile); }
	}
	
	void load() {
		try
		{
			if (!File.Exists<ScienceManager>(configFile)) { return; }
			ConfigNode cn = ConfigNode.Load(configFile);
			Vector2 pos = cn.GetValueDefault("windowPos", new Vector2(windowPosSize.x, windowPosSize.y));
			if (pos != Vector2.zero) {
				windowPosSize.x = pos.x;
				windowPosSize.y = pos.y;
			}
			else {
				windowPosSize = new Rect(Screen.width / 2 - windowPosSize.width / 2, Screen.height / 2 - windowPosSize.height / 2, 0, 0);
			}
		}
		catch (Exception ex)
		{
			Debug.Log("Science Manager: Error while loading config: " + ex.Message);
		}
	}

	void resetSimulation() {
		subjectScienceValue.Clear();
		subjectScience.Clear();
	}
	
	float simulateScience(ScienceData data, bool asTransmission = false) {
		var subject = ResearchAndDevelopment.GetSubjectByID(data.subjectID);
		var id = (asTransmission ? "X-" : "") + data.subjectID;
		if (subjectScienceValue.ContainsKey(id)) {
			float science = Mathf.Min((data.dataAmount * (asTransmission ? data.transmitValue : 1)) / subject.dataScale * subject.subjectValue * subjectScienceValue[id], subject.scienceCap);
			subjectScience[id] += science;
			subjectScienceValue[id] = Mathf.Max(0f, 1f - subjectScience[id] / subject.scienceCap);
			return science;
		}
		else {
			float science = Mathf.Min((data.dataAmount * (asTransmission ? data.transmitValue : 1)) / subject.dataScale * subject.subjectValue * subject.scientificValue, subject.scienceCap);
			subjectScience.Add(id, subject.science + science);
			subjectScienceValue.Add(id, Mathf.Max(0f, 1f - subjectScience[id] / subject.scienceCap));
			return science;
		}
	}

    string tooltipText = "";
	
	void drawGUI() {
		var vessel = FlightGlobals.ActiveVessel;
		if (vessel.FindPartModulesImplementing<KerbalEVA>().Count > 0) {
			vessel = lastVessel;
		}
		else if (lastVessel != vessel) {
			lastVessel = vessel;
			assistants.Clear();
		}
		if (vessel == null) {
			return;
		}
		
		GUI.skin = HighLogic.Skin;
		
		if (GUI.Button (new Rect (Screen.width - 260, -1, 60, 21), "Science")) {
			windowShown = !windowShown;
			if (!windowShown) { save(); }
		}
		
		if (windowShown) {
			windowPosSize.x = Mathf.Clamp(windowPosSize.x, -1, Screen.width - windowPosSize.width);
			windowPosSize.y = Mathf.Clamp(windowPosSize.y, -1, Screen.height - windowPosSize.height);
			windowPosSize = GUILayout.Window(10007, windowPosSize, windowGUI, windowTitle + " - " + ResearchAndDevelopment.Instance.Science, GUILayout.MinWidth(650));


            DrawToolTip();
                  
		}
	}
	
	void drawContainer(ModuleScienceContainer container) {
		bool hasData = container.GetData().Length > 0;
		if (!hasData) { return; }
		
		var list = new List<ScienceData>(container.GetData());
		list.Sort(delegate(ScienceData a, ScienceData b) { return string.Compare(a.title, b.title); });
		
		foreach (var data in list) {
			var sv = simulateScience(data);
			var tsv = simulateScience(data, true);
			dataAmount += data.dataAmount;
			returnScience += sv;
			transmitScience += tsv;
			string stats = string.Format("( {0:F1} / {1:F1} )", sv, tsv);
			
			GUILayout.BeginVertical();
			
			GUILayout.BeginHorizontal(GUILayout.MaxHeight(18));
			GUILayout.Label(container.part.partInfo.title + (stats != "" ? " - " + stats : ""), styleTitle);
			GUILayout.Label(data.title, styleTitle);
			GUILayout.EndHorizontal();
			
			GUILayout.EndVertical();
		}
	}
	
	void drawExperiment(ModuleScienceExperiment experiment) {
		var page = pages.Find(p => p.host == experiment.part);
		var data = experiment.GetData()[0];
		bool hasData = data != null;
		string stats = "";
		
		if (hasData) {
			var sv = simulateScience(data);
			var tsv = simulateScience(data, true);
			dataAmount += data.dataAmount;
			returnScience += sv;
			transmitScience += tsv;
			stats = string.Format("( {0:F1} / {1:F1} )", sv, tsv);
			
			if (page != null) {
				var sub = ResearchAndDevelopment.GetSubjectByID(data.subjectID);
				if (sub.science < sub.scienceCap) {
					page.OnKeepData(page.pageData);
				}
				else {
					page.OnDiscardData(page.pageData);
				}
			}
		}


        ScienceExperiment exp = ResearchAndDevelopment.GetExperiment(experiment.experiment.id);
        string tooltip = "";
  
        CelestialBody body = experiment.vessel.mainBody;
        foreach (ExperimentSituations sit in Enum.GetValues(typeof(ExperimentSituations)))
        {
            if (exp.IsAvailableWhile(sit, body) &&
                 !((sit == ExperimentSituations.FlyingHigh || sit == ExperimentSituations.FlyingLow) && !body.atmosphere) &&
                 !(sit == ExperimentSituations.SrfSplashed && !body.ocean)
               )
            {
                string key;
                if (exp.BiomeIsRelevantWhile(sit))                    
                {
                    if (body.BiomeMap != null && body.BiomeMap.Attributes != null)
                        foreach (CBAttributeMap.MapAttribute biome in body.BiomeMap.Attributes)
                        {
                            ScienceSubject sub = ResearchAndDevelopment.GetExperimentSubject(exp, sit, body, biome.name);                            
                            tooltip += body.name + " " + sit.ToString() + " " + biome.name + " " + sub.science.ToString("F1") + "/" + Mathf.RoundToInt(sub.scienceCap) + "\n";
                            
                        }
                    if (body.name == "Kerbin")
                    {
                        string[] specials = { "KSC", "Runway", "Launchpad" };
                        foreach (string special in specials)
                        {
                            ScienceSubject sub = ResearchAndDevelopment.GetExperimentSubject(exp, sit, body, special);
                            tooltip += body.name + " " + sit.ToString() + " " + special + " " + sub.science.ToString("F1") + "/" + Mathf.RoundToInt(sub.scienceCap) + "\n";

                        }
                    }
                }
                else
                {
                    key = body.name + sit.ToString();
                    ScienceSubject sub = ResearchAndDevelopment.GetExperimentSubject(exp, sit, body, "");
                    if (sub != null)
                        tooltip += body.name + " " + sit.ToString() + " " +  sub.science.ToString("F1") + "/" + Mathf.RoundToInt(sub.scienceCap) + "\n";
                    else
                        tooltip += body.name + " " + sit.ToString() + " 0/" + Mathf.RoundToInt(exp.scienceCap) + "\n";
                }
            }
        }
        
        
		GUILayout.BeginVertical();
		
		GUILayout.BeginHorizontal(GUILayout.MaxHeight(18));
        GUILayout.Label(new GUIContent(experiment.experimentID + (stats != "" ? " - " + stats : ""), tooltip), styleTitle);
		if (hasData) {
			GUILayout.Label(data.title, styleTitle);
		}
		GUILayout.FlexibleSpace();
		if (GUILayout.Button((hasData ? "Reset" : "Deploy"), new GUILayoutOption[] { GUILayout.MaxHeight(18), GUILayout.ExpandWidth(false) })) {
			if (hasData) {
				experiment.ResetExperiment();
			}
			else {
				experiment.DeployExperiment();
//				experiment.StartCoroutine((IEnumerator)miGatherData.Invoke(experiment, new object[] { false })); // KSP doesn't like window free results yet
			}
		}
		GUILayout.Space(5);
		GUILayout.EndHorizontal();
		
		GUILayout.EndVertical();
	}
	
	void windowGUI(int ID) {
		resetSimulation();
		
		var exps = lastVessel.FindPartModulesImplementing<ModuleScienceExperiment>();
		exps.Sort(delegate(ModuleScienceExperiment a, ModuleScienceExperiment b) { return string.Compare(a.experimentID, b.experimentID); });
		
		var conts = lastVessel.FindPartModulesImplementing<ModuleScienceContainer>();
		conts.Sort(delegate(ModuleScienceContainer a, ModuleScienceContainer b) { return string.Compare(a.part.partName, b.part.partName); });
		
		if (ExperimentsResultDialog.Instance != null) {
			pages = (List<ExperimentResultDialogPage>)fiERDP.GetValue(ExperimentsResultDialog.Instance);
		}
		else {
			pages = new List<ExperimentResultDialogPage>();
		}
		
		returnScience = transmitScience = dataAmount = 0;
//		var comm = vessel.FindPartModulesImplementing<ModuleDataTransmitter>().Find(pm => !pm.IsBusy());
		var comm = (from c in lastVessel.FindPartModulesImplementing<ModuleDataTransmitter>() where !c.IsBusy() orderby c.DataResourceCost select c).FirstOrDefault();
		
		scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(Screen.height / 2 > 400 ? Screen.height / 2 : 400));
		conts.ForEach(c => drawContainer(c));
		exps.ForEach(e => drawExperiment(e));
		GUILayout.EndScrollView();
		
		GUILayout.BeginHorizontal();
		GUILayout.Label(string.Format("Return: {0:F1} - Transmission: {1:F1} - Energy Cost: {2:F1}", returnScience, transmitScience, (comm != null ? dataAmount * comm.DataResourceCost : 0)));
		GUILayout.FlexibleSpace();
		if (!auto) {
			if (GUILayout.Button("Deploy All", new GUILayoutOption[] { GUILayout.MaxHeight(21), GUILayout.ExpandWidth(false) })) {
				exps.FindAll(e => !e.Deployed).ForEach(e => e.DeployExperiment());
			}
			if (GUILayout.Button("Send All", new GUILayoutOption[] { GUILayout.MaxHeight(21), GUILayout.ExpandWidth(false) })) {
				if (comm != null) {
					resetSimulation();
					foreach (var e in exps) {
						var data = e.GetData()[0];
						if (data == null) { continue; }
						var subject = ResearchAndDevelopment.GetSubjectByID(data.subjectID);
						if (subject.science == subject.scienceCap) {
							miDumpData.Invoke(e, new object[] {});
							e.ResetExperiment();
						}
					}
					comm.StartTransmission();
				}
			}
		}
		if (GUILayout.Button((auto ? "Stop" : "Auto"), new GUILayoutOption[] { GUILayout.MaxHeight(21), GUILayout.Width(40) })) {
			auto = !auto;
			if (!auto) {
				assistants.Clear();
			}
		}
		GUILayout.EndHorizontal();

        if (Event.current.type != EventType.Layout)
            tooltipText = GUI.tooltip;

		GUI.DragWindow();
	}


    private void DrawToolTip()
    {
        if (tooltipText != "")
        {
            GUIStyle tooltipStyle = new GUIStyle(styleTitle);
            tooltipStyle.border = new RectOffset(3, 3, 3, 3);
            tooltipStyle.padding = new RectOffset(3, 3, 3, 3);

            //tooltipStyle.normal.background = GUI.skin.box.normal.background;

            Vector2 ttSize = styleTitle.CalcSize(new GUIContent(tooltipText));
            GUI.Box(new Rect(Event.current.mousePosition.x, Event.current.mousePosition.y, ttSize.x + 10, ttSize.y + 10), tooltipText, tooltipStyle);
            GUI.depth = 0;
        }
    }

	
	public void Update() {
		if (!auto) {
			return;
		}
		if (assistants.Count == 0) {
			foreach (var e in lastVessel.FindPartModulesImplementing<ModuleScienceExperiment>()) {
				if (assistants.Find(ea => ea.experiment.experimentID == e.experimentID) == null) {
					assistants.Add(new ExperimentAssistant(e));
				}
			}
		}
		assistants.FindAll(a => a.delay > 0).ForEach(a => a.delay -= Time.deltaTime);
		assistants.FindAll(a => a.ready).ForEach(a => a.Deploy());
		var assistant = (from a in assistants where a.deployed && !a.transmitting && a.transmitValue > 0 orderby a.transmitValue descending select a).FirstOrDefault();
		if (assistant != null) {
			assistant.Transmit();
		}
		assistants.FindAll(a => a.deployed && a.transmitValue == 0).ForEach(a => a.Reset(5));
	}
}