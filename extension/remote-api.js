(function remoteApi() {
    const ENDPOINT = "ws://127.0.0.1:7979/spicetify";
    const PUSH_INTERVAL = 1000;
    const SETTLE_DELAY = 150;
    const REPEAT_MODES = ["off", "context", "track"];

    class TrackReader {
        static image(item) {
            const meta = item.metadata || {};
            const images = item.album?.images || item.images || [];
            const pick = images.find(image => image.label === "xlarge") || images.find(image => image.label === "large") || images[0];
            const raw = meta.image_xlarge_url || pick?.url || meta.image_large_url || meta.image_url || null;
            return raw && raw.startsWith("spotify:image:") ? `https://i.scdn.co/image/${raw.slice(14)}` : raw;
        }

        static artists(item) {
            if (Array.isArray(item.artists) && item.artists.length) return item.artists.map(artist => artist.name);
            const meta = item.metadata || {};
            const names = [];
            for (let index = 0; ; index++) {
                const name = meta[index === 0 ? "artist_name" : `artist_name:${index}`];
                if (!name) return names;
                names.push(name);
            }
        }

        static read(item) {
            if (!item?.uri) return null;
            const meta = item.metadata || {};
            const parts = item.uri.split(":");
            return {
                uri: item.uri,
                id: parts[2] || null,
                type: parts[1] || null,
                name: item.name || meta.title || null,
                artists: TrackReader.artists(item),
                album: item.album?.name || meta.album_title || null,
                album_uri: item.album?.uri || meta.album_uri || null,
                image: TrackReader.image(item),
                duration_ms: item.duration?.milliseconds ?? (Number(meta.duration) || null)
            };
        }
    }

    class LyricsProvider {
        constructor(limit) {
            this.limit = limit;
            this.cache = new Map();
        }

        async load(track) {
            if (!track || track.type !== "track" || !track.id) return { track_uri: track?.uri || null, sync_type: "NONE", lines: [] };
            if (this.cache.has(track.id)) return this.cache.get(track.id);
            const url = `https://spclient.wg.spotify.com/color-lyrics/v2/track/${track.id}?format=json&vocalRemoval=false&market=from_token`;
            let body;
            try {
                body = await Spicetify.CosmosAsync.get(url, null, { "app-platform": "WebPlayer" });
            } catch {
                return { track_uri: track.uri, sync_type: "NONE", lines: [] };
            }
            const lyrics = body?.lyrics || {};
            const result = {
                track_uri: track.uri,
                sync_type: lyrics.syncType || "NONE",
                provider: lyrics.provider || null,
                language: lyrics.language || null,
                lines: (lyrics.lines || []).map(line => ({
                    start_ms: Number(line.startTimeMs) || 0,
                    end_ms: Number(line.endTimeMs) || null,
                    text: line.words
                }))
            };
            if (this.cache.size >= this.limit) this.cache.delete(this.cache.keys().next().value);
            this.cache.set(track.id, result);
            return result;
        }
    }

    class PlayerController {
        constructor(lyrics) {
            this.player = Spicetify.Player;
            this.lyrics = lyrics;
            this.actions = {
                status: () => null,
                play: args => args.uri ? this.player.playUri(args.uri) : this.player.play(),
                pause: () => this.player.pause(),
                toggle: () => this.player.togglePlay(),
                next: () => {
                    this.allow("canSkipNext", "next");
                    return this.player.next();
                },
                previous: () => this.player.back(),
                seek: args => this.seek(args),
                volume: args => this.volume(args),
                mute: args => this.mute(args),
                shuffle: args => this.shuffle(args),
                repeat: args => this.repeat(args),
                like: args => this.like(args),
                lyrics: () => this.lyrics.load(this.track())
            };
        }

        flag(value, current) {
            if (value === undefined || value === null || value === "") return !current;
            return value === true || value === "true" || value === "1" || value === 1 || value === "on";
        }

        allow(key, label) {
            if (this.player.data?.restrictions?.[key] === false) throw new Error(`${label} is not allowed right now`);
        }

        capabilities() {
            const restrictions = this.player.data?.restrictions || {};
            return {
                next: restrictions.canSkipNext !== false,
                previous: restrictions.canSkipPrevious !== false,
                seek: restrictions.canSeek !== false,
                shuffle: restrictions.canToggleShuffle !== false,
                repeat_context: restrictions.canToggleRepeatContext !== false,
                repeat_track: restrictions.canToggleRepeatTrack !== false
            };
        }

        track() {
            return TrackReader.read(this.player.data?.item);
        }

        state() {
            const player = this.player;
            const track = this.track();
            return {
                playing: Boolean(track) && player.isPlaying(),
                position_ms: track ? player.getProgress() : 0,
                duration_ms: track ? player.getDuration() : 0,
                volume: Math.round(player.getVolume() * 100),
                muted: player.getMute(),
                shuffle: player.getShuffle(),
                repeat: REPEAT_MODES[player.getRepeat()] || "off",
                liked: player.getHeart(),
                can: this.capabilities(),
                track,
                timestamp: Date.now()
            };
        }

        seek(args) {
            const duration = this.player.getDuration();
            let target;
            if (args.position_ms !== undefined) target = Number(args.position_ms);
            else if (args.percent !== undefined) target = duration * Number(args.percent) / 100;
            else if (args.delta_ms !== undefined) target = this.player.getProgress() + Number(args.delta_ms);
            if (!Number.isFinite(target)) throw new Error("seek requires position_ms, percent or delta_ms");
            this.allow("canSeek", "seek");
            target = Math.min(Math.max(Math.round(target), 0), Math.max(duration - 500, 0));
            this.player.seek(target < 2 ? 0 : target);
        }

        volume(args) {
            let target;
            if (args.volume !== undefined) target = Number(args.volume);
            else if (args.delta !== undefined) target = this.player.getVolume() * 100 + Number(args.delta);
            if (!Number.isFinite(target)) throw new Error("volume requires volume (0-100) or delta");
            this.player.setVolume(Math.min(Math.max(target, 0), 100) / 100);
        }

        mute(args) {
            const current = this.player.getMute();
            const target = this.flag(args.state, current);
            if (target === current) return;
            const helper = Spicetify.Platform?.PlaybackAPI?.getMuteHelper?.();
            if (helper) target ? helper.mute() : helper.unmute();
            else document.querySelector('[data-testid="volume-bar-toggle-mute-button"]')?.click();
        }

        shuffle(args) {
            const current = this.player.getShuffle();
            const target = this.flag(args.state, current);
            if (target === current) return;
            this.allow("canToggleShuffle", "shuffle");
            this.player.setShuffle(target);
        }

        repeat(args) {
            const mode = args.mode === undefined ? (this.player.getRepeat() + 1) % 3 : REPEAT_MODES.indexOf(String(args.mode));
            if (mode < 0) throw new Error("repeat mode must be off, context or track");
            if (mode === 1) this.allow("canToggleRepeatContext", "repeat");
            if (mode === 2) this.allow("canToggleRepeatTrack", "repeat track");
            this.player.setRepeat(mode);
        }

        like(args) {
            const current = this.player.getHeart();
            if (this.flag(args.state, current) !== current) this.player.toggleHeart();
        }

        async run(action, args) {
            const handler = this.actions[action];
            if (!handler) throw new Error(`unknown action ${action}`);
            const data = await handler(args || {});
            if (data !== undefined && data !== null) return data;
            if (action !== "status") await new Promise(resolve => setTimeout(resolve, SETTLE_DELAY));
            return this.state();
        }
    }

    class BridgeClient {
        constructor(controller) {
            this.controller = controller;
            this.socket = null;
            this.retry = 500;
        }

        start() {
            this.connect();
            Spicetify.Player.addEventListener("songchange", () => this.push());
            Spicetify.Player.addEventListener("onplaypause", () => this.push());
            setInterval(() => this.push(), PUSH_INTERVAL);
        }

        connect() {
            const socket = new WebSocket(ENDPOINT);
            this.socket = socket;
            socket.onopen = () => {
                this.retry = 500;
                this.push();
            };
            socket.onmessage = event => this.receive(event.data);
            socket.onclose = () => {
                if (this.socket !== socket) return;
                this.socket = null;
                setTimeout(() => this.connect(), this.retry);
                this.retry = Math.min(this.retry * 2, 5000);
            };
            socket.onerror = () => socket.close();
        }

        send(message) {
            if (this.socket?.readyState === WebSocket.OPEN) this.socket.send(JSON.stringify(message));
        }

        push() {
            if (this.socket?.readyState === WebSocket.OPEN) this.send({ type: "state", state: this.controller.state() });
        }

        async receive(raw) {
            let message;
            try {
                message = JSON.parse(raw);
            } catch {
                return;
            }
            if (message.type !== "command") return;
            try {
                const data = await this.controller.run(message.action, message.args);
                this.send({ type: "result", id: message.id, ok: true, data });
            } catch (error) {
                this.send({ type: "result", id: message.id, ok: false, error: String(error?.message || error) });
            }
            this.push();
        }
    }

    function boot() {
        if (!Spicetify?.Player?.addEventListener || !Spicetify.CosmosAsync) {
            setTimeout(boot, 300);
            return;
        }
        new BridgeClient(new PlayerController(new LyricsProvider(64))).start();
    }

    boot();
})();
