﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryBot.Model;
using System;
using VkNet.Model;
using VkNet.Model.GroupUpdate;
using VkNet.Utils;

namespace StoryBot.Logic
{
    public class EventsHandler
    {
        private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly ReplyHandler reply;

        public EventsHandler(ReplyHandler reply)
        {
            this.reply = reply;
        }

        public void MessageNewEvent(JObject jObject)
        {
            var message = Message.FromJson(new VkResponse(jObject));
            var peerId = message.PeerId.Value;

            if (reply.CheckThatMessageIsLast(message))
            {
                try
                {
                    if (!string.IsNullOrEmpty(message.Payload))
                    {
                        var button = JsonConvert.DeserializeObject<MessagePayload>(message.Payload).Button;
                        if (!string.IsNullOrEmpty(button))
                        {
                            reply.ReplyToNumber(peerId, int.Parse(button));
                        }
                    }
                    else if (message.Text[0] == MessageBuilder.Prefix)
                    {
                        reply.ReplyToCommand(peerId, message.Text.Remove(0, 1).ToLower());
                    }
                    else if (int.TryParse(message.Text, out int number))
                    {
                        reply.ReplyToNumber(peerId, number);
                    }
                    else if (message.Text.ToLower() == "начать")
                    {
                        reply.ReplyFirstMessage(peerId);
                    }
                }
                catch (Exception exception)
                {
                    reply.ReplyWithError(peerId, exception);
                    throw;
                }
            }
            else
            {
                logger.Debug($"Ignoring old message ({message.Date.ToString()}) from {message.PeerId}");
            }
        }

        public void MessageAllowEvent(JObject jObject)
        {
            reply.ReplyFirstMessage(MessageAllow.FromJson(new VkResponse(jObject)).UserId.Value);
        }
    }
}
