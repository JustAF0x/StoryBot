﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryBot.Model;
using System;
using System.Linq;
using System.Text;
using VkNet.Abstractions;
using VkNet.Enums.SafetyEnums;
using VkNet.Model;
using VkNet.Model.Keyboard;
using VkNet.Model.RequestParams;
using VkNet.Utils;

namespace StoryBot.Logic
{
    public class MessagesHandler
    {
        private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        private static readonly char prefix = '.'; 

        private readonly IVkApi vkApi;

        private readonly DatabaseHandler database;

        public MessagesHandler(IVkApi vkApi, DatabaseHandler database)
        {
            this.vkApi = vkApi;
            this.database = database;
        }

        /// <summary>
        /// Handles new message
        /// </summary>
        /// <param name="messageObject"></param>
        public void HandleNew(JObject messageObject)
        {
            var content = Message.FromJson(new VkResponse(messageObject));
            long peerId = content.PeerId.Value;

            try
            {
                // Make sure it is the last message
                if (vkApi.Messages.GetHistory(new MessagesGetHistoryParams { Count = 1, PeerId = peerId }).Messages.ToCollection()[0].Date <= content.Date)
                {
                    if (content.Text[0] == prefix)
                    {
                        string[] command = content.Text.Remove(0, 1).ToLower().Split(" ");
                        HandleCommand(peerId, command[0], command.Skip(1).ToArray());
                    }
                    else if (content.Payload != null)
                    {
                        if (content.Payload[0] == prefix)
                        {
                            string[] command = content.Payload.Remove(0, 1).ToLower().Split(" ");
                            HandleCommand(peerId, command[0], command.Skip(1).ToArray());
                        }
                        else
                        {
                            HandleKeyboard(peerId, JsonConvert.DeserializeObject<Payload>(content.Payload).Button);
                        }
                    }
                    else if (int.TryParse(content.Text, out int number))
                    {
                        HandleNumber(peerId, number);
                    }
                }
                else
                {
                    logger.Info($"Ignoring old message ({content.Date.ToString()}) from {content.PeerId}");
                }
            }
            catch (Exception exception)
            {
                vkApi.Messages.Send(new MessagesSendParams
                {
                    RandomId = new DateTime().Millisecond,
                    PeerId = peerId,
                    Message = $"Во время обработки вашего сообщения произошла непредвиденная ошибка:\n\n{exception.Message}\n\nПожалуйста сообщите администрации"
                });
                throw;
            }
        }
        
        // Senders //

        /// <summary>
        /// Sends "Hello, world!"
        /// </summary>
        /// <param name="peerId"></param>
        private void SendHelloWorld(long peerId)
        {
            vkApi.Messages.Send(new MessagesSendParams
            {
                RandomId = new DateTime().Millisecond,
                PeerId = peerId,
                Message = "Hello, world!"
            });
        }

        /// <summary>
        /// Sends basic message with content and options
        /// </summary>
        /// <param name="peerId"></param>
        /// <param name="progress"></param>
        /// <param name="story"></param>
        private void SendContent(long peerId, SaveProgress progress, StoryDocument story = null)
        {
            if (story == null)
            {
                story = database.GetChapter((int)progress.Story, (int)progress.Chapter);
            }
            SaveDocument save = database.GetSave(peerId);
            if (progress.Storyline != "Ending")
            {
                save.Current = progress;

                StorylineElement storylineElement = Array.Find(story.Storylines, x => x.Tag == progress.Storyline).Elements[progress.Position];

                StringBuilder stringBuilder = new StringBuilder();
                if (progress.Achievement != null)
                {
                    var achievement = story.Achievements[(int)progress.Achievement];
                    stringBuilder.Append($"Вы заработали достижение {achievement.Name}!\n - {achievement.Description}\n\n");
                    save.AddAchievement(story.Id, story.Chapter, (int)progress.Achievement);
                }
                database.UpdateSave(save);
                foreach (string x in storylineElement.Content)
                {
                    stringBuilder.Append(x + "\n");
                }
                stringBuilder.Append("\n");

                KeyboardBuilder keyboardBuilder = new KeyboardBuilder(true);
                for (int i = 0; i < storylineElement.Options.Length; i++)
                {
                    var x = storylineElement.Options[i];

                    stringBuilder.Append($"[ {i + 1} ] {x.Content}\n");

                    keyboardBuilder.AddButton($"[ {i + 1} ]",
                        System.Web.HttpUtility.JavaScriptStringEncode(JsonConvert.SerializeObject(new SaveProgress
                        {
                            Story = progress.Story,
                            Chapter = progress.Chapter,
                            Storyline = x.Storyline ?? progress.Storyline,
                            Position = x.Position,
                            Achievement = x.Achievement
                        })),
                        KeyboardButtonColor.Default);
                }

                vkApi.Messages.Send(new MessagesSendParams
                {
                    RandomId = new DateTime().Millisecond,
                    PeerId = peerId,
                    Message = stringBuilder.ToString(),
                    Keyboard = keyboardBuilder.Build()
                });
            }
            else // Ending
            {
                save.AddEnding(story.Id, story.Chapter, progress.Position);
                database.UpdateSave(save);

                StringBuilder stringBuilder = new StringBuilder();

                if (story.Chapter != 0) // Prologue check
                {
                    StoryEnding ending = story.Endings[progress.Position];

                    foreach (string x in ending.Content)
                    {
                        stringBuilder.Append(x + "\n");
                    }

                    int alternativeEndingsCount = story.Endings.Length - 1;
                    if (progress.Position == 0) // Check if ending canonical
                    {
                        stringBuilder.Append($"\nПоздравляем, вы получили каноничную концовку \"{ending.Name}\"!\n\n");
                        stringBuilder.Append($"Эта глава содержит еще {alternativeEndingsCount} альтернативные концовки.");
                    }
                    else // Alternative
                    {
                        stringBuilder.Append($"\nПоздравляем, вы получили альтернативную концовку \"{ending.Name}\"!\n\n");
                        stringBuilder.Append($"Эта глава содержит еще {alternativeEndingsCount - 1} альтернативные концовки и одну каноничную.");
                    }
                }
                else // If it is a prologue
                {
                    stringBuilder.Append("\nПоздравляем, вы завершили пролог!");
                }

                vkApi.Messages.Send(new MessagesSendParams
                {
                    RandomId = new DateTime().Millisecond,
                    PeerId = peerId,
                    Message = stringBuilder.ToString()
                });
                SendChapterChoiceDialog(peerId, story.Id);
            }
        }

        /// <summary>
        /// Sends short stats
        /// </summary>
        /// <param name="peerId"></param>
        private void SendStats(long peerId)
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("Общая статистика:\n\n");

            var save = database.GetSave(peerId);
            foreach (var s in database.GetAllPrologues())
            {
                int completedChapters;
                try
                {
                    completedChapters = Array.Find(save.StoriesStats, x => x.StoryId == s.Id).Chapters.Length;
                }
                catch (NullReferenceException)
                {
                    completedChapters = 0;
                }
                stringBuilder.Append($"- {s.Name}: {completedChapters}/{database.GetAllChapters(s.Id).Count}\n");
            }

            vkApi.Messages.Send(new MessagesSendParams
            {
                RandomId = new DateTime().Millisecond,
                PeerId = peerId,
                Message = stringBuilder.ToString()
            });
        }

        /// <summary>
        /// Sends story stats
        /// </summary>
        /// <param name="peerId"></param>
        /// <param name="storyId"></param>
        private void SendStats(long peerId, int storyId)
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append($"Статистика по \"{database.GetPrologue(storyId).Name}\":\n");

            SaveChapterStats[] chapters;
            try
            {
                chapters = Array.Find(database.GetSave(peerId).StoriesStats, x => x.StoryId == storyId).Chapters;
                for (int i = 0; i < chapters.Length; i++)
                {
                    var chapter = database.GetChapter(storyId, i);
                    stringBuilder.Append($"- {i + 1}: {chapters[i].ObtainedEndings}/{chapter.Endings.Length}, {chapters[i].ObtainedAchievements}/{chapter.Achievements.Length}\n");
                }
            }
            catch (NullReferenceException)
            {
                stringBuilder.Append("\n- Нет данных.");
            }

            vkApi.Messages.Send(new MessagesSendParams
            {
                RandomId = new DateTime().Millisecond,
                PeerId = peerId,
                Message = stringBuilder.ToString()
            });
        }

        /// <summary>
        /// Send chapter stats
        /// </summary>
        /// <param name="peerId"></param>
        /// <param name="storyId"></param>
        /// <param name="chapterId"></param>
        private void SendStats(long peerId, int storyId, int chapterId)
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append($"Статистика по главе {chapterId + 1} истории \"{database.GetPrologue(storyId).Name}\":\n\n");
           
            try
            {
                var chapterSave = Array.Find(database.GetSave(peerId).StoriesStats, x => x.StoryId == storyId).Chapters[chapterId];
                var chapterData = database.GetChapter(storyId, chapterId);

                stringBuilder.Append($"Полученные концовки ({chapterSave.ObtainedEndings.Length}/{chapterData.Endings.Length}):");
                for (int i = 0; i < chapterData.Endings.Length; i++)
                {
                    if (chapterSave.ObtainedEndings.Contains(i))
                    {
                        string type;
                        if (i == 0)
                            type = "ОСН";
                        else
                            type = "АЛЬТ";
                        stringBuilder.Append($"- [{type}] {chapterData.Endings[i].Name}\n");
                    }
                }

                stringBuilder.Append($"\nПолученные достижения ({chapterSave.ObtainedAchievements.Length}/{chapterData.Achievements.Length}):");
                for (int i = 0; i < chapterData.Endings.Length; i++)
                {
                    if (chapterSave.ObtainedAchievements.Contains(i))
                    {
                        stringBuilder.Append($"- {chapterData.Endings[i].Name}\n");
                    }
                }
            }
            catch (NullReferenceException)
            {
                stringBuilder.Append("- Нет данных.");
            }

            vkApi.Messages.Send(new MessagesSendParams
            {
                RandomId = new DateTime().Millisecond,
                PeerId = peerId,
                Message = stringBuilder.ToString()
            });
        }

        // Dialogs //

        /// <summary>
        /// Sends a quest choosing dialog
        /// </summary>
        /// <param name="peerId"></param>
        private void SendStoryChoiceDialog(long peerId)
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("Выберите историю:\n");

            KeyboardBuilder keyboardBuilder = new KeyboardBuilder(true);
            var storiesList = database.GetAllPrologues();
            for (int i = 0; i < storiesList.Count; i++)
            {
                stringBuilder.Append($"[ {i + 1} ] {storiesList[i].Name}\n");
                keyboardBuilder.AddButton(
                    $"[ {i + 1} ]",
                    System.Web.HttpUtility.JavaScriptStringEncode(JsonConvert.SerializeObject(new SaveProgress
                    {
                        Story = i
                    })),
                    KeyboardButtonColor.Primary);
            }

            var save = database.GetSave(peerId);
            save.Current = new SaveProgress();
            database.UpdateSave(save);

            vkApi.Messages.Send(new MessagesSendParams
            {
                RandomId = new DateTime().Millisecond,
                PeerId = peerId,
                Message = stringBuilder.ToString(),
                Keyboard = keyboardBuilder.Build()
            });
        }

        /// <summary>
        /// Sends a chapter choosing dialog
        /// </summary>
        /// <param name="peerId"></param>
        /// <param name="storyId"></param>
        private void SendChapterChoiceDialog(long peerId, int storyId)
        {
            StringBuilder stringBuilder = new StringBuilder();
            KeyboardBuilder keyboardBuilder = new KeyboardBuilder(true);

            var chaptersList = database.GetAllChapters(storyId);

            stringBuilder.Append($"Выберите главу истории {chaptersList[0].Name} или используйте команду {prefix}reset для выбора другой истории:\n");

            // For prologue
            stringBuilder.Append($"Глава 0. Пролог\n");
            keyboardBuilder.AddButton(
                $"[ Пролог ]",
                System.Web.HttpUtility.JavaScriptStringEncode(JsonConvert.SerializeObject(new SaveProgress
                {
                    Story = storyId,
                    Chapter = 0,
                    Storyline = chaptersList[0].Beginning,
                    Position = 0
                })),
                KeyboardButtonColor.Primary);
            
            // For other chapters
            for (int i = 1; i < chaptersList.Count; i++)
            {
                var chapter = chaptersList[i];
                stringBuilder.Append($"Глава {i}. {chapter.Name}\n");
                keyboardBuilder.AddButton(
                    $"[ {i} ]",
                    System.Web.HttpUtility.JavaScriptStringEncode(JsonConvert.SerializeObject(new SaveProgress
                    {
                        Story = storyId,
                        Chapter = i,
                        Storyline = chapter.Beginning,
                        Position = 0
                    })),
                    KeyboardButtonColor.Primary);
            }

            vkApi.Messages.Send(new MessagesSendParams
            {
                RandomId = new DateTime().Millisecond,
                PeerId = peerId,
                Message = stringBuilder.ToString(),
                Keyboard = keyboardBuilder.Build()
            });
        }

        // Handles //

        /// <summary>
        /// Handles message if it is a number
        /// </summary>
        /// <param name="peerId"></param>
        /// <param name="number"></param>
        private void HandleNumber(long peerId, int number)
        {
            try
            {
                SaveProgress progress = database.GetSave(peerId).Current;

                StoryDocument story;
                if (progress.Story != null)
                {
                    if (progress.Chapter != null)
                    {
                        story = database.GetChapter((int)progress.Story, (int)progress.Chapter);

                        StoryOption storyOption = Array
                            .Find(story.Storylines, x => x.Tag == (progress.Storyline ?? story.Beginning))
                            .Elements[progress.Position]
                            .Options[number - 1];

                        progress.Storyline = storyOption.Storyline ?? progress.Storyline;
                        progress.Position = storyOption.Position;
                        progress.Achievement = storyOption.Achievement;
                    }
                    else
                    {
                        story = database.GetChapter((int)progress.Story, number);

                        progress.Chapter = number;
                        progress.Storyline = story.Beginning;
                        progress.Position = 0;
                    }
                }
                else
                {
                    var save = database.GetSave(peerId);
                    number--;
                    save.Current.Story = number;
                    database.UpdateSave(save);
                    SendChapterChoiceDialog(peerId, number);
                    return;
                }
                SendContent(peerId, progress, story);
            }
            catch (IndexOutOfRangeException)
            {
                vkApi.Messages.Send(new MessagesSendParams
                {
                    RandomId = new DateTime().Millisecond,
                    PeerId = peerId,
                    Message = "Выберите вариант из представленных"
                });
            }
        }

        /// <summary>
        /// Handles command with arguments
        /// </summary>
        /// <param name="peerId"></param>
        /// <param name="command"></param>
        /// <param name="args"></param>
        private void HandleCommand(long peerId, string command, string[] args = null)
        {
            switch (command)
            {
                case "helloworld":
                    SendHelloWorld(peerId);
                    break;
                case "repeat":
                    var progress = database.GetSave(peerId).Current;
                    if (progress.Story == null)
                    {
                        SendStoryChoiceDialog(peerId);
                    }
                    else if (progress.Chapter == null)
                    {
                        SendChapterChoiceDialog(peerId, (int)progress.Story);
                    }
                    else
                    {
                        SendContent(peerId, progress);
                    }
                    break;
                case "reset":
                    SendStoryChoiceDialog(peerId);
                    break;
                case "stats":
                    switch (args.Length)
                    {
                        case 0:
                            SendStats(peerId);
                            break;
                            case 1:
                            SendStats(peerId, int.Parse(args[0]) - 1);
                            break;
                        case 2:
                            SendStats(peerId, int.Parse(args[0]) - 1, int.Parse(args[1]) - 1);
                            break;
                        default:
                            break;
                    }
                    break;
                default:
                    StringBuilder stringBuilder = new StringBuilder("Список команд:\n\n");
                    stringBuilder.AppendLine(prefix + "reset - Сбрасывает текущую главу и открывает диалог выбора истории и главы");
                    stringBuilder.AppendLine();
                    stringBuilder.AppendLine(prefix + "repeat - Заново отправляет сообщений с диалогом выбора для текущей истории");
                    stringBuilder.AppendLine();
                    stringBuilder.AppendLine(prefix + "stats - Отправляет общую статистику по всем историям");
                    stringBuilder.AppendLine(prefix + "stats <номер_истории> - Отправляет статистику по выбранной истории");
                    stringBuilder.AppendLine(prefix + "stats <номер_истории> <номер_главы> - Отправляет статистику по выбранной главе");

                    vkApi.Messages.Send(new MessagesSendParams
                    {
                        RandomId = new DateTime().Millisecond,
                        PeerId = peerId,
                        Message = stringBuilder.ToString()
                    });
                    break;
            }
        }

        /// <summary>
        /// Handles message with keyboard payload
        /// </summary>
        /// <param name="peerId"></param>
        /// <param name="_payload"></param>
        private void HandleKeyboard(long peerId, string _payload)
        {
            SaveProgress payload = JsonConvert.DeserializeObject<SaveProgress>(_payload);

            if (payload.Chapter != null)
            {
                SendContent(peerId, payload);
            }
            else
            {
                SendChapterChoiceDialog(peerId, (int)payload.Story);
            } 
        }
    }
}