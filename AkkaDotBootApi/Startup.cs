﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Akka.Actor;
using AkkaDotBootApi.Actor;
using AkkaDotModule.ActorSample;
using AkkaDotModule.ActorUtils;
using AkkaDotModule.Config;
using AkkaDotModule.Kafka;
using AkkaDotModule.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using IApplicationLifetime = Microsoft.Extensions.Hosting.IApplicationLifetime;

namespace AkkaDotBootApi
{
    public class Startup
    {
        private string AppName = "AkkaDotBootApi";
        private string Company = "웹노리";
        private string CompanyUrl = "http://wiki.webnori.com/pages/viewpage.action?pageId=42467383";
        private string DocUrl = "http://wiki.webnori.com/display/AKKA";

        protected ActorSystem actorSystem;

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();

            services.AddSingleton<ConsumerSystem>();
            services.AddSingleton<ProducerSystem>();

            // Akka 셋팅
            var envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var akkaConfig = AkkaLoad.Load(envName, Configuration);
            actorSystem = ActorSystem.Create("AkkaDotBootSystem", akkaConfig);            
            services.AddAkka(actorSystem);

            // Swagger
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Version = "v1",
                    Title = AppName,
                    Description = $"{AppName} ASP.NET Core Web API",
                    TermsOfService = new Uri(CompanyUrl),
                    Contact = new OpenApiContact
                    {
                        Name = Company,
                        Email = "psmon@live.co.kr",
                        Url = new Uri(CompanyUrl),
                    },
                    License = new OpenApiLicense
                    {
                        Name = $"Document",
                        Url = new Uri(DocUrl),
                    }
                });

                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = SecuritySchemeType.ApiKey,
                    Scheme = "Bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description = "JWT Authorization header using the Bearer scheme."
                });

                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                          new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "Bearer"
                                }
                            },
                            new string[] {}
                    }
                });

                // Set the comments path for the Swagger JSON and UI.
                var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                c.IncludeXmlComments(xmlPath);
            });

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IApplicationLifetime lifetime)
        {
            app.UseSwagger();

            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", AppName + "V1");
                c.RoutePrefix = "help";
            });

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });

            lifetime.ApplicationStarted.Register(() =>
            {
                // HelloActor 기본액터
                AkkaLoad.RegisterActor("helloActor" /*AkkaLoad가 인식하는 유니크명*/,
                    actorSystem.ActorOf(Props.Create(() => new HelloActor("webnori")), "helloActor" /*AKKA가 인식하는 Path명*/
                ));

                var helloActor = actorSystem.ActorSelection("user/helloActor");
                var helloActor2 = AkkaLoad.ActorSelect("helloActor");

                helloActor.Tell("hello");
                helloActor2.Tell("hello");


                // 밸브 Work : 초당 작업량을 조절                
                int timeSec = 1;
                int elemntPerSec = 5;
                var throttleWork = AkkaLoad.RegisterActor("throttleWork", 
                    actorSystem.ActorOf(Props.Create(() => new ThrottleWork(elemntPerSec, timeSec)), "throttleWork"));

                // 실제 Work : 밸브에 방출되는 Task를 개별로 처리
                var worker = AkkaLoad.RegisterActor("worker", actorSystem.ActorOf(Props.Create<WorkActor>(), "worker"));

                // 배브의 작업자를 지정
                throttleWork.Tell(new SetTarget(worker));

                // KAFKA 셋팅
                // 각 System은 싱글톤이기때문에 DI를 통해 Controller에서 참조획득가능
                var consumerSystem = app.ApplicationServices.GetService<ConsumerSystem>();
                var producerSystem = app.ApplicationServices.GetService<ProducerSystem>();

                //소비자 : 복수개의 소비자 생성가능
                consumerSystem.Start(new ConsumerAkkaOption()
                {
                    KafkaGroupId = "testGroup",
                    KafkaUrl = "kafka:9092",
                    RelayActor = null,          //소비되는 메시지가 지정 액터로 전달되기때문에,처리기는 액터로 구현
                    Topics = "akka100"
                });

                //생산자 : 복수개의 생산자 생성가능
                producerSystem.Start(new ProducerAkkaOption()
                {
                    KafkaUrl = "kafka:9092",
                    ProducerName = "producer1"
                });

                List<string> messages = new List<string>();
                //보너스 : 생산의 속도를 조절할수 있습니다.
                int tps = 10;
                producerSystem.SinkMessage("producer1", "akka100", messages, tps);

            });
        }
    }
}
