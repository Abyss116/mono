//
// TaskEngine.cs: Class that executes each task.
//
// Author:
//   Marek Sieradzki (marek.sieradzki@gmail.com)
// 
// (C) 2005 Marek Sieradzki
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

#if NET_2_0

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;
using System.Xml;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.BuildEngine {
	internal class TaskEngine {
		
		ITask		task;
		XmlElement	taskElement;
		Type		taskType;
		Project		parentProject;
		
		static Type	requiredAttribute;
		static Type	outputAttribute;
		
		static TaskEngine ()
		{
			requiredAttribute = typeof (Microsoft.Build.Framework.RequiredAttribute);
			outputAttribute = typeof (Microsoft.Build.Framework.OutputAttribute);
		}

		public TaskEngine (Project project)
		{
			parentProject = project;
		}
		
		public void Prepare (ITask task, XmlElement taskElement,
				     IDictionary <string, string> parameters, Type taskType)
		{
			Dictionary <string, object>	values;
			PropertyInfo	currentProperty;
			PropertyInfo[]	properties;
			object		value;
			
			this.task = task;
			this.taskElement = taskElement;
			this.taskType = taskType;
			values = new Dictionary <string, object> ();
			
			foreach (KeyValuePair <string, string> de in parameters) {
				currentProperty = taskType.GetProperty (de.Key);
				if (currentProperty == null)
					throw new InvalidProjectFileException (String.Format ("Task does not have property \"{0}\" defined",
						de.Key));
				
				value = GetObjectFromString (de.Value, currentProperty.PropertyType);		
				
				if (value != null)
					values.Add (de.Key, value);
			}
			
			properties = taskType.GetProperties ();
			foreach (PropertyInfo pi in properties) {
				if (pi.IsDefined (requiredAttribute, false) && values.ContainsKey (pi.Name) == false)
					throw new InvalidProjectFileException (String.Format ("Required property '{0}'not set.", pi.Name));
				
				if (values.ContainsKey (pi.Name))
					InitializeParameter (pi, values [pi.Name]);
			}
		}
		
		public bool Execute ()
		{
			return task.Execute ();
		}
		
		public void PublishOutput ()
		{
			XmlElement	xmlElement;
			PropertyInfo	propertyInfo;
			string		propertyName;
			string		taskParameter;
			string		itemName;
			object		o;
		
			foreach (XmlNode xmlNode in taskElement.ChildNodes) {
				if (!(xmlNode is XmlElement))
					continue;
			
				xmlElement = (XmlElement) xmlNode;
				
				if (xmlElement.Name != "Output")
					throw new InvalidProjectFileException ("Only Output elements can be Task's child nodes.");
				if (xmlElement.GetAttribute ("ItemName") != String.Empty && xmlElement.GetAttribute ("PropertyName") != String.Empty)
					throw new InvalidProjectFileException ("Only one of ItemName and PropertyName attributes can be specified.");
				if (xmlElement.GetAttribute ("TaskParameter") == String.Empty)
					throw new InvalidProjectFileException ("TaskParameter attribute must be specified.");
					
				taskParameter = xmlElement.GetAttribute ("TaskParameter");
				itemName = xmlElement.GetAttribute ("ItemName");
				propertyName = xmlElement.GetAttribute ("PropertyName");
				
				propertyInfo = taskType.GetProperty (taskParameter);
				if (propertyInfo == null)
					throw new Exception ("Could not get property info.");
				if (!propertyInfo.IsDefined (outputAttribute, false))
					throw new Exception ("This is not output property.");
				
				o = propertyInfo.GetValue (task, null);
				if (o == null)
					continue;
				
				if (itemName != String.Empty) {
					PublishItemGroup (propertyInfo, o, itemName);
				} else {
					PublishProperty (propertyInfo, o, propertyName);
				}
			}
		}
		
		void InitializeParameter (PropertyInfo propertyInfo, object value)
		{
			propertyInfo.SetValue (task, value, null);
		}

		void PublishItemGroup (PropertyInfo propertyInfo,
					       object o,
					       string itemName)
		{
			BuildItemGroup newItems = CollectItemGroup (propertyInfo, o, itemName);
			
			if (parentProject.EvaluatedItemsByName.ContainsKey (itemName)) {
				BuildItemGroup big = parentProject.EvaluatedItemsByName [itemName];
				big.Clear ();
				parentProject.EvaluatedItemsByName.Remove (itemName);
				parentProject.EvaluatedItemsByName.Add (itemName, newItems);
			} else {
				parentProject.EvaluatedItemsByName.Add (itemName, newItems);
			}
			foreach (BuildItem bi in newItems)
				parentProject.EvaluatedItems.AddItem (bi);
		}
		
		void PublishProperty (PropertyInfo propertyInfo,
					      object o,
					      string propertyName)
		{
			BuildProperty bp = CollectProperty (propertyInfo, o, propertyName);
			parentProject.EvaluatedProperties.AddProperty (bp);
		}
		
		BuildProperty CollectProperty (PropertyInfo propertyInfo, object o, string name)
		{
			string output = null;
			BuildProperty bp;
			
			if (propertyInfo == null)
				throw new ArgumentNullException ("propertyInfo");
			if (o == null)
				throw new ArgumentNullException ("o");
			if (name == null)
				throw new ArgumentNullException ("name");
			
			if (propertyInfo.PropertyType == typeof (ITaskItem)) {
				ITaskItem item = (ITaskItem) o;
				bp = ChangeType.TransformToBuildProperty (name, item);
			} else if (propertyInfo.PropertyType == typeof (ITaskItem[])) {
				ITaskItem[] items = (ITaskItem[]) o;
				bp = ChangeType.TransformToBuildProperty (name, items);
			} else {
				if (propertyInfo.PropertyType.IsArray == true) {
					output = ChangeType.TransformToString ((object[])o, propertyInfo.PropertyType);
			} else {
					output = ChangeType.TransformToString (o, propertyInfo.PropertyType);
				}
				bp = ChangeType.TransformToBuildProperty (name, output);
			}
			return bp;
		}
		
		BuildItemGroup CollectItemGroup (PropertyInfo propertyInfo, object o, string name)
		{
			BuildItemGroup big;
			string temp;
			
			if (propertyInfo == null)
				throw new ArgumentNullException ("propertyInfo");
			if (o == null)
				throw new ArgumentNullException ("o");
			if (name == null)
				throw new ArgumentNullException ("name");
				
			if (propertyInfo.PropertyType == typeof (ITaskItem)) {
				ITaskItem item = (ITaskItem) o;
				big = ChangeType.TransformToBuildItemGroup (name, item);
			} else if (propertyInfo.PropertyType == typeof (ITaskItem[])) {
				ITaskItem[] items = (ITaskItem[]) o;
				big = ChangeType.TransformToBuildItemGroup (name, items);
			} else {
				if (propertyInfo.PropertyType.IsArray == true) {
					temp = ChangeType.TransformToString ((object[]) o, propertyInfo.PropertyType);
				} else {
					temp = ChangeType.TransformToString (o, propertyInfo.PropertyType);
				}
				big = ChangeType.TransformToBuildItemGroup (name, temp);
			}
			return big;	
		}
				
		object GetObjectFromString (string raw, Type type)
		{
			Expression e;
			object result;
			
			e = new Expression ();
			e.Parse (raw, true);
			
			if ((string) e.ConvertTo (parentProject, typeof (string)) == String.Empty)
				return null;
			
			result = e.ConvertTo (parentProject, type);
			
			return result;
		}
	}
}

#endif
