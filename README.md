# Synapse
EXSII featured a truly interactive live event, enabled by Synapse, a cutting-edge mod that brings players from around the world together to engage with innovative new maps in real-time.
Participants compete against each other on a shared leaderboard and can communicate seamlessly using the in-game chat feature.
### https://exsii.totalbs.dev/

## Features
- Takeover the main menu with your own prefabs during the countdown to build hype.
- A countdown and custom banner from the main menu, along with notifications from anywhere in-game that your event is live.
- A 1-hour grace period to automatically download required mods for an event.
- Multiple divisions so all players can enjoy their preferred difficulty.
- A fully featured chatroom where players can interact with each other, complete with moderation tools like banning malicious users, as well as toggleable options like a profanity filter or opting-out of chat entirely.
- Replace the lobby with a custom prefab themed around your event, which also includes custom cinematics that can play as an intro or outro.
- Download and synchronously start maps for all players to experience maps at the same time.
- An event leaderboard where players can compete with each other, as well as the ability to run tournament formats which can eliminate players each round.
- All dockerized to be easily portable.
- Seamlessly runs, even with 1900+ players, as shown during EXSII.

## Setup for event hosts
#### Interested in running an event using Synapse? Contact me and I can help config and list your event using the official API.

### Setup docker
If you havent already install docker from https://www.docker.com/ on a server with a web adress or HTTPS: ip.

Create the following folder format 
```txt
synapse/
 ├─ listing/
 │   ├─ wwwroot/
 │   └─ appsettings.Production.json
 └─ server/
     ├─ config/
     └─ appsettings.Production.json
docker-compose.yml
```

In your docker-compose.yml you will configure your docker instulation. 
The way to do this is by creating 2 services, Synapse listener and Synapse Server

Here is a example config for docker.

```yml
services:
  synapse-listing:
    image: ghcr.io/aeroluna/synapse-listing
    container_name: synapse-listing
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://synapse-listing:1000
      - ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
    volumes:
      - ./synapse/listing/wwwroot:/app/wwwroot
      - ./synapse/listing/appsettings.Production.json:/app/appsettings.Production.json
    restart: unless-stopped

  synapse-server:
    image: ghcr.io/aeroluna/synapse-server
    container_name: synapse-server
    depends_on:
      - synapse-listing
    environment:
      - DOTNET_ENVIRONMENT=Production
    volumes:
      - ./synapse/server/appsettings.Production.json:/app/appsettings.Production.json
      - ./synapse/server/config:/config
    ports:
      - 1001:1001
    stdin_open: true
    tty: true
```

Next up your gonna want to setup your settings files. Create 2 files in your /listening and /server directory named ```appsettings.Production.json```

Each of those files will contain different information. For the listening server you will create your "Event" Instulation. That includes a name date ip and all of that.

Example of listening appsettings

```json
{
  "Listing": {
    "Title": "Local Test Event",
    "Time": "2025-12-09T00:00:00Z",
    "IpAddress": "192.168.40.185:1001",
    "BannerImage": "",
    "BannerColor": "#341552",
    "GameVersion": "1.40.8",
    "Divisions": []
  }
}
```

Divisions are used to create 2 catagories but thats not fully needed configured.

Next step is to configure your server config. This one will store all map locations, Every banner or image and store the admin ID's

Example
```json
{
  "Port": 1001, 
  "Listing": "http://synapse-listing:1000/api/v1/directory", 
  "Directory": "/config", 
  "Auth": {
    "Test": { 
      "Enabled": false
    },
    "Steam": {
      "Enabled": true,
      "APIKey": "000000000" 
    },
    "Oculus": {
      "Enabled": true
    }
  },
  "Event": {
    "Title": "Local Test Event", 
    "Format": "None",
    "Intro": { 
      "Motd": "<color=#ff6464><size=120%><mspace=0.6em>//// <b><color=#ffffff>CONNECTION ESTABLISHED</color></b> ////", 
      "Intermission": "00:05:00",
      "Duration": "00:01:00", 
      "Url": "http://localhost:5033/images/intro.png"
    },
    "Finish": {
      "Motd": "<color=#ff6464><size=120%><mspace=0.6em>//// <b><color=#ffffff>CONNECTION TERMINATED</color></b> ////<br><color=#ffffff>YOUR COOPERATION IS APPRECIATED",
      "Url": "http://localhost:5033/images/finish.png"
    },
    "Maps": [
      {
        "Name": "Breezer", 
        "AltCoverUrl": "https://localhost:5033/maps/alternative_breezer.png", 
        "Motd": "<size=120%><#FFFFFF>[<#FF2121><b>ERROR</b><#FFFFFF> @ <#3171E8>02:18:23<#FFFFFF> | Vivify] Map file <#45B543>'breezer.zip'<#FFFFFF> has been breached by unknown source",
        "Intermission": "00:10:00", 
        "Duration": "00:10:00",
        "Ruleset": {
          "AllowResubmission": true, 
          "Modifiers": ["noEnergy"] 
        },
        "Keys": [
          {
            "Characteristic": "Standard",
            "Difficulty": 1 
          },
          {
            "Characteristic": "Standard",
            "Difficulty": 2
          }
        ],
        "Downloads": [
          {
            "GameVersion": "1.29.1", 
            "Url": "https://localhost:5033/maps/Breezer_2019_3d3304c.zip",
            "Hash": "3d3304c27e4cd48cb0f9da768ce833a2" 
          },
          {
            "GameVersion": "1.34.2,1.37.1,1.39.1,1.40.0",
            "Url": "https://localhost:5033/maps/Breezer_2021_2c170c1.zip",
            "Hash": "2c170c14544b25b055029d2ca67b932c"
          }
        ]
      }
    ]
  }
}
```
If you read through that you would notice there is a section called "Steam API key" Steam has a thing where when a user joins this is used to check if there a admin. You can create a API webook key here https://steamcommunity.com/dev/apikey
Just paste in the key and it will work

Once thats all configured. We have 1 more thing to do and thats setup your admin users. This is the easiest step

Go to your server and into your config. Your gonna create 2 files. Admin.json and roles.json
This stores your admin roles and who is admin


## Admins.json

```json
[
  {
    "roles": ["coordinator"],
    "id": "USERID_Steam",
    "username": "ADMIN USERS STEAM NAME"
  }
]
```

## Roles.json

```json
[
  {
    "name": "coordinator",
    "priority": 99,
    "color": "red",
    "permission": 1
  },
  {
    "name": "moderator",
    "priority": 50,
    "color": "yellow",
    "permission": 2
  },
  {
    "name": "noqualify",
    "priority": 10,
    "color": "gray",
    "permission": 4
  }
]
```


once that is all configured start the docker container by navigating to your directory in powershell for windows or in cmdline in linux and run `docker compose up -d`
Then run `docker logs synapse server` to make sure it all booted correctly. Enjoy!


Nested lists represent subcommands, e.g. `scores backup reload`.
### Client
- `motd [message]` Prints the motd again or sets a new one. Allows rich text. Requires `coordinator` to set an motd
- `roll [min] [max]` Rolls a random number. Rolls between 1-100 with no parameters, and between 1-MAX with one parameter, and MIN-MAX with two parameters.
- `tell [player] [message]` (`t`, `whisper`, `w`) Privately message another player. Messages will still be logged by the server.
- `who [options] [player]` Prints how many players are currently connected. May specify a name to find all players whose name starts with that name. `-e` to print more names. `-v` to print IDs (requires `moderator`).
- `ping` Prints current latency between client and server. (Client only)
### Message
- `say [message]` Sends a priority message to everyone with the format `[Server] MESSAGE`. Allows rich text. Requires `coordinator`.
- `sayraw [message]` Sends a priority message without formatting. Allows rich text. Requires `coordinator`.
### Users
- `allow [player]` Adds a user to the whitelist. Requires `moderator`.
- `ban [player] [reason] [time]` Bans a user. Optionally set a reason and/or duration. Requires `moderator`.
- `banip [player]` Bans a user by ip. Requires `moderator`.
- `kick [player]` Kicks a user. Requires `moderator`.
- `blacklist` Requires `moderator`.
  - `reload` Reloads `blacklist.json` from disk.
  - `list` Lists currently banned users.
  - `add [id] [username]` Manually add an ID/username to the blacklist.
  - `remove [options] [username]` Remove a user from the blacklist. `-i` to search by ID instead.
- `bannedips` Requires `moderator`.
  - `reload` Reloads `bannedips.json` from disk.
  - `list` Lists currently banned ips.
  - `add [ip]` Manually add an IP to the blacklist.
  - `remove [ip]` Remove an IP from the blacklist.
- `roles`  Requires `coordinator`.
  - `reload` Reloads `roles.json` and `admins.json` from disk.
  - `list` Lists all admins and their roles.
  - `listroles` Lists all roles.
  - `add [options] [username] [role]` Add a role to a user. `-i` to search by ID instead.
  - `remove [options] [username] [role]` Remove a role to a user. `-i` to search by ID instead.
- `whitelist`  Requires `moderator`.
  - `reload` Reloads `whitelist.json` from disk.
  - `list` Lists all whitelisted users.
  - `add [id] [username]` Manually add an ID/username to the whitelist.
  - `remove [options] [username] [role]` Remove a user from the whitelist. `-i` to search by ID instead.
### Scores
- `scores` Requires `coordinator`. Refer to divisions by index, i.e. 0 = Casual, 1 = Experienced. 
  - `refresh [map index]` Resends map's leaderboard to all players. Uses current map index if not specified.
  - `remove [options] [division] [map index] [username]` Removes a score. Uses current map index if not specified. `-i` to search by ID instead.
  - `drop [division] [map index]` Drops all scores for a map. Uses current map index if not specified.
  - `resubmit [map index]` Resubmit scores for the map to the tournament format.
  - `list [options] [division] [map index]` List all submitted scores. Uses current map index if not specified. `-v` to print the scores, `-e` to print more.
  - `test` Submit fake scores for the current map.
  - `backup`
    - `reload` Reload score backups from disk.
### Event
- `event` Requires `coordinator`.
  - `status` Displays the current status of the event.
  - `start [seconds]` Starts the intermission for the current stage. Uses time from config if not specified.
  - `play [seconds]` Plays the current stage. Uses time from config if not specified.
  - `stop` Stops the current stage. Will kick users out of intros, outros and levels.
  - `stage [stage index]` Changes the stage. Can use `n` or `p` instead of an index for "next" or "previous" respectively.
  - `index [options] [map index]` Changes the map. Can use `n` or `p` instead of an index for "next" or "previous" respectively. `-s` to additionally submit scores to the tournament format. `-a` to auto-start the next with the time from the config.

## TestClient
Synapse.TestClient is a simple client designed to emulate chatting and setting scores. Input the URL to the listing in the `appsettings.ENVIRONMENT.json`.
Comes with the following commands:
- `stop` Disconnect all clients and close.
- `deploy [count]` Deploys a specific amount of clients to connect to the server.
- `score` Command all clients to submit a random score for the current map.
- `send` Command all clients to start sending random chat messages.
- `roll` Command all clients to send the roll command to the server.
