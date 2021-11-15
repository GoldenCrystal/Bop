# Bop

Bop is a small Windows tool that shares iTunes currently playing song information over a WebSocket.
This information can then be retrieved and displayed on a webpage, which can be embedded in other places such as OBS Studio's Browser Source.

Embedded within Bop is a simple HTML page that connects to the WebSocket and renders track information. The HTML and CSS can easily be customized to achieve one's desired look.
The default theme colors can easily be modified by updating `theme-override.css`.

Thanks to the WebSocket communication and client-side JavaScript, all fields will be updated simultaneously and quickly, allowing for a nicer and richer visual experience.
(Such a thing would not be possible if the data was pushed to separate files, as is the case with other tools)

Out of the box, the rendering will look as below:

![Example of a song displayed using the default layout](screenshots/default-theme.png "A song displayed using the default layout")

# Using the tool

## Run the tool

Currently, the tool is a command line ASP.NET Core application. I suggest running it within Windows Terminal, but anything is fine as long as it sits well with you.

Upon startup, Bop will start iTunes or connect to the currently running iTunes instance. In order for Bop to continue working, you should not close the iTunes Window.

## Display the current track

At this step, and assuming you didn't change the embedded server's URL, you can either navigate your browser to http://localhost:9696/ or use this URL with OBS Studio's browser source.
Changes to the track that is currently playing in iTunes should be reflected immediately.

# Configuration

## Update the URL used by the embedded server

By default, the tool will expose itself using HTTP over port 9696.
You can change this by updating the value of the `Kestrel.Endpoints.Http.Url` setting in `appsettings.json`.

## Alter the provided HTML and CSS files to craft your own appearance

The tool is provided with an out of the box layout for displaying the currently played track:

- `index.html` This file will be displayed if you access the tool from a web browser (e.g. http://localhost:9696/). This is the URL that you want to use with OBS Studio's Browser Source.
- `main.css` This is the default theme provided with the tool. It features a basic layout, as well as CSS animations and a few customizable variables.
- `theme-overrides.css` This file can be modified to alter the colors of the default theme. Change colors here if the default layout is fine with you and you only want to set your own colors.

Using the default layout, it is recommended to display the page in a `514x130` viewport, or wider if desired.
Colors and transition durations are defined as css variables that you can change at one place.

Default layout:

![Image of the default layout](screenshots/default-theme.png "Default layout appearance")

Default layout with modified colors:

![Image of the default layout with altered colors](screenshots/default-theme-customized.png "Default layout appearance with modified colors")

# Build

To build the tool, you will need:

- An installation of iTunes (Store version is fine, but make sure you don't try accessing it from an elevated account, as this might not work)
- The desktop version of MSBuild 17 or later (MSBuild 17 is provided with Visual Studio 2022)

# Credits

This tool draw inspiration from [Snip](https://github.com/dlrudie/Snip), without which I'd never have learned that iTune was accessible with a simple COM API.
