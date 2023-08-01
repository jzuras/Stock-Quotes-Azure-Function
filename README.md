# Stock-Quotes-Azure-Function


This is part of a new version of my original Stock Quotes repo, continuing my Learning Journey.
This part is the 2 Azure Functions, written in C#. As with the original JS repo, this is not
expected to be useful to the world at large.

The new-to-me parts of this project were the Azure Functions, of course, but also reading
from a RESTful API in C# as opposed to JS or TS. 

The Azure extension for VS Code was obviously a big help, but I ran into a snag based on the
Color Theme I was using in VS Code, which was Solarized Light. The icon for Azure Functions
is basically hidden for this Theme, so as I was following along with a tutorial I was unable
to get it to work. Finally I noticed the icon when the mouse hovered over it, so I changed
the Color Theme and was able to move on. (This was the impetus to finally get me over to
the Dark (theme) Side.)

One significant benefit to using an Azure Function is that I no longer need to ask the user for
the api key, which I was doing to avoid checking the key itself into git. Now the key is
set up within Azure, and locally on my development PC.

Originally, I wrote this to be a simple pass-through of the TwelveData api, even though
I knew it was a pain to work with. But when I started working on a new Client for this
Azure Function, I realized I had the chance to build something that was easier for the
client to use. Comparing the original TS client to the new one makes this clear, I believe.

I go into more detail on this in my discussion on [LinkedIn](https://www.linkedin.com/feed/update/urn:li:share:7092217565648723968/)
