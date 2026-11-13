---
id: setup
---

# Setup

**Requirements**
- Unity 2022.3 (the version the [VRChat Creator Companion](https://vcc.docs.vrchat.com/) installs)
- The VRChat Worlds SDK, 3.8.1 or newer

**Installation**

UdonSharp CE is distributed through its own VPM listing. It replaces UdonSharp in a project, so a project
must not have both packages installed.

## Add the UdonSharp CE listing to the VCC
- Install the latest version of the [Creator Companion](https://vrchat.com/home/download).
- Open Settings → Packages → Add Repository.
- Add `https://vrc.furroxide.dev/vpm/index.json` and confirm.

## Add UdonSharp CE to a world project
- Add the project to the VCC, migrating it if necessary, and select it from the Projects listing.
- If the project lists **UdonSharp**, remove it first.
- Find **UdonSharp CE** in the package list and press "Add".

## Add UdonSharp CE with the CLI
[The CLI](https://vcc.docs.vrchat.com/vpm/cli/) is a tool for advanced users, and the best way to manage VPM projects on non-Windows systems for now.
- Add the listing with `vpm add repo https://vrc.furroxide.dev/vpm/index.json`.
- Then [add the package](https://vcc.docs.vrchat.com/vpm/cli/#add-package) `com.merlin.udonsharp` to your project.

**Getting started**

1. Make a new object in your scene
2. Add an Udon Behaviour component to your object
3. Below the "New Program" button click the dropdown and select "Udon C# Program Asset"
4. Now click the New Program button, this will create a new UdonSharp program asset for you
5. Click the Create Script button and choose a save destination and name for the script.
6. This will create a template script that's ready for you to start working on, open the script in your editor of choice and start programming

**Asset explorer asset creation**

Instead of creating assets from an UdonBehaviour you can also do the following:
1. Right-click in your project asset explorer
2. Navigate to Create > U# script
3. Click U# script, this will open a create file dialog
4. Choose a name for your script and click Save
5. This will create a .cs script file and an UdonSharp program asset that's set up for the script in the same directory
